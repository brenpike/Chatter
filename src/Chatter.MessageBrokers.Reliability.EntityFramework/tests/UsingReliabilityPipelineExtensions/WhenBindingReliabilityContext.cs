using Chatter.CQRS.Commands;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingReliabilityPipelineExtensions
{
    // These tests pin the single-context contract of the reliability extensions. Each extension takes its own
    // TContext, and Replace is remove-then-add, so before this contract existed a host that wrote
    // WithInboxBehavior<A>() followed by WithOutboxProcessingBehavior<B>() resolved IUnitOfWork as UnitOfWork<B>
    // while IBrokeredMessageInbox still claimed the message id in A, flushing that claim into A's transaction -
    // the unit of work then committed a different DbContext than the one holding the claim, and nothing said so.
    // The contract makes that registration unrepresentable: it is refused at registration, before anything is
    // mutated.
    public class WhenBindingReliabilityContext : Testing.Core.Context
    {
        // The resolved sequence is outermost-first, because CommandBehaviorPipeline composes last-to-first.
        private static readonly Type[] _orderIndependentSequence = new[]
        {
            typeof(OutboxProcessingBehavior<TestCommand>),
            typeof(UnitOfWorkBehavior<TestCommand>),
            typeof(InboxBehavior<TestCommand>)
        };

        // This module's static Extensions type cannot be named in source from here. Its full name collides with the
        // framework NAMESPACE Microsoft.Extensions.DependencyInjection.Extensions, and because the two live in
        // different assemblies the collision is CS0434 - an error, which no qualifier and no alias settles. Looking
        // the type up by name inside the assembly that declares it sidesteps source-level name binding entirely;
        // throwOnError makes a rename of the type or its namespace fail here rather than silently sweep nothing.
        private static readonly Type _reliabilityExtensions = typeof(ReliabilityContextBinding).Assembly
            .GetType("Microsoft.Extensions.DependencyInjection.Extensions", throwOnError: true);

        // CommandPipelineBuilder's constructor is internal, so a real builder over a caller-owned collection is
        // reached through the public AddChatterCqrs seam. The collection is passed in rather than created here so a
        // refused call can be inspected after the exception has propagated out.
        private static void ConfigurePipeline(IServiceCollection services, Action<CommandPipelineBuilder> configure)
            => services.AddChatterCqrs(Mock.Of<IConfiguration>(), configure);

        // Every refusal must name BOTH contexts - the one already bound and the one just asked for. A message
        // naming only one leaves an operator guessing which of their extension calls to change.
        private static void RefuseMixedContexts(Action<CommandPipelineBuilder> configure)
        {
            var services = new ServiceCollection();
            Action register = () => ConfigurePipeline(services, configure);

            register.Should().Throw<InvalidOperationException>()
                    .Which.Message.Should().Contain(typeof(PrimaryDbContext).FullName)
                                  .And.Contain(typeof(SecondaryDbContext).FullName);
        }

        // Resolution is a pure DI exercise: the reliability collaborators are stubbed AFTER the pipeline is
        // configured so the last descriptor for each of them wins, and no DbContext is ever constructed.
        private static IReadOnlyList<Type> ResolveBehaviorSequence(IServiceCollection services)
        {
            services.AddLogging();
            services.AddScoped(_ => Mock.Of<IUnitOfWork>());
            services.AddScoped(_ => Mock.Of<IBrokeredMessageInbox>());
            services.AddScoped(_ => Mock.Of<IOutboxProcessor>());

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            return scope.ServiceProvider
                .GetRequiredService<IEnumerable<ICommandBehavior<TestCommand>>>()
                .Select(behavior => behavior.GetType())
                .ToList();
        }

        private static IReadOnlyList<ServiceDescriptor> FindContextBindings(IServiceCollection services)
            => services.Where(descriptor => !descriptor.IsKeyedService
                                            && descriptor.ServiceType == typeof(ReliabilityContextBinding))
                       .ToList();

        // The defect this closes, exactly as an application would have met it.
        [Fact]
        public void MustRefuseAnOutboxContextThatDiffersFromTheBoundInboxContext()
        {
            RefuseMixedContexts(builder =>
            {
                builder.WithInboxBehavior<PrimaryDbContext>();
                builder.WithOutboxProcessingBehavior<SecondaryDbContext>();
            });
        }

        [Fact]
        public void MustRefuseAnInboxContextThatDiffersFromTheBoundOutboxContext()
        {
            RefuseMixedContexts(builder =>
            {
                builder.WithOutboxProcessingBehavior<PrimaryDbContext>();
                builder.WithInboxBehavior<SecondaryDbContext>();
            });
        }

        [Fact]
        public void MustRefuseAUnitOfWorkContextThatDiffersFromTheBoundInboxContext()
        {
            RefuseMixedContexts(builder =>
            {
                builder.WithInboxBehavior<PrimaryDbContext>();
                builder.WithUnitOfWorkBehavior<SecondaryDbContext>();
            });
        }

        // Retention participates in the same contract. Its purge deletes inbox and outbox rows through its own
        // TContext, so purging a context other than the one holding those rows is the same defect wearing a
        // different hat - it would silently leave the real tables to grow without bound.
        [Fact]
        public void MustRefuseARetentionContextThatDiffersFromTheBoundUnitOfWorkContext()
        {
            RefuseMixedContexts(builder =>
            {
                builder.WithUnitOfWorkBehavior<PrimaryDbContext>();
                builder.WithReliabilityRetention<SecondaryDbContext>(options => options.InboxDeduplicationWindow = TimeSpan.FromDays(1));
            });
        }

        [Fact]
        public void MustRefuseAnInboxContextThatDiffersFromTheBoundRetentionContext()
        {
            RefuseMixedContexts(builder =>
            {
                builder.WithReliabilityRetention<PrimaryDbContext>(options => options.ProcessedOutboxRetention = TimeSpan.FromDays(3));
                builder.WithInboxBehavior<SecondaryDbContext>();
            });
        }

        // A refused call must leave nothing behind. Were the binding checked partway through an extension, the
        // host would be left holding a half-registered pipeline that a caller catching the exception could still
        // build a provider over.
        [Fact]
        public void MustLeaveTheServiceCollectionExactlyAsItWasWhenACallIsRefused()
        {
            var services = new ServiceCollection();
            List<ServiceDescriptor> beforeTheRefusedCall = null;

            Action register = () => ConfigurePipeline(services, builder =>
            {
                builder.WithInboxBehavior<PrimaryDbContext>();
                beforeTheRefusedCall = builder.Services.ToList();
                builder.WithOutboxProcessingBehavior<SecondaryDbContext>();
            });

            register.Should().Throw<InvalidOperationException>();

            beforeTheRefusedCall.Should().NotBeNull();
            services.Should().Equal(beforeTheRefusedCall,
                                    "a refused call must not register anything - the same descriptors must still sit in the same slots");
        }

        // Every entry point, every order, repeated: one binding, and a pipeline that still resolves in the
        // documented order. This is what proves the binding marker and the retention options descriptor are both
        // invisible to the behavior-order normalization that runs alongside them.
        [Fact]
        public void MustBindOnceAcrossEveryEntryPointForOneContext()
        {
            var services = new ServiceCollection();
            ConfigurePipeline(services, builder =>
            {
                builder.WithOutboxProcessingBehavior<PrimaryDbContext>();
                builder.WithReliabilityRetention<PrimaryDbContext>(options => options.InboxDeduplicationWindow = TimeSpan.FromDays(1));
                builder.WithInboxBehavior<PrimaryDbContext>();
                builder.WithUnitOfWorkBehavior<PrimaryDbContext>();
                builder.WithInboxBehavior<PrimaryDbContext>();
                builder.WithOutboxProcessingBehavior<PrimaryDbContext>();
            });

            var bindings = FindContextBindings(services);

            bindings.Should().ContainSingle("the binding is registered by the first extension called and read by every later one");
            bindings.Single().ImplementationInstance.Should().BeOfType<ReliabilityContextBinding>()
                    .Which.ContextType.Should().Be(typeof(PrimaryDbContext));

            services.Count(descriptor => !descriptor.IsKeyedService
                                         && descriptor.ServiceType == typeof(IHostedService)
                                         && descriptor.ImplementationType == typeof(ReliabilityRetentionPurgeService<PrimaryDbContext>))
                    .Should().Be(1, "retention was asked for once");

            ResolveBehaviorSequence(services).Should().Equal(_orderIndependentSequence);
        }

        // Binding must not displace what the extensions already did. A lone inbox call still brings its own unit
        // of work over the very context it was given.
        [Fact]
        public void MustStillSelfRegisterTheUnitOfWorkForALoneInboxCall()
        {
            var services = new ServiceCollection();
            ConfigurePipeline(services, builder => builder.WithInboxBehavior<PrimaryDbContext>());

            services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(IUnitOfWork))
                    .Which.ImplementationType.Should().Be(typeof(UnitOfWork<PrimaryDbContext>));
        }

        // A sweep rather than a list: every public TContext-parameterized entry point this module offers is
        // discovered and driven with a second context. An entry point added later that forgets to bind fails HERE,
        // instead of shipping as the same silent mismatch this contract exists to eliminate.
        [Fact]
        public void MustRefuseASecondContextFromEveryContextParameterizedEntryPoint()
        {
            var entryPoints = ContextParameterizedEntryPoints();

            entryPoints.Select(entryPoint => entryPoint.Name).Should().Contain(new[]
            {
                "WithUnitOfWorkBehavior",
                "WithInboxBehavior",
                "WithOutboxProcessingBehavior",
                "WithReliabilityRetention"
            }, "a discovery filter that matched nothing would make this sweep pass without driving anything");

            foreach (var entryPoint in entryPoints)
            {
                var services = new ServiceCollection();
                Action register = () => ConfigurePipeline(services, builder =>
                {
                    builder.WithInboxBehavior<PrimaryDbContext>();
                    InvokeWithContext(entryPoint, builder, typeof(SecondaryDbContext), suppliedConfiguration: null);
                });

                register.Should().Throw<InvalidOperationException>(
                            $"'{entryPoint.Name}' must bind the reliability context before it registers anything")
                        .Which.Message.Should().Contain(typeof(PrimaryDbContext).FullName)
                                      .And.Contain(typeof(SecondaryDbContext).FullName);
            }
        }

        // The one discovery filter both sweeps run on. An entry point added later is picked up by each of them
        // without being listed anywhere, and the name check in the sweep above is what keeps a filter that matched
        // nothing from making either of them pass while driving no entry point at all.
        private static IReadOnlyList<MethodInfo> ContextParameterizedEntryPoints()
            => _reliabilityExtensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                     .Where(IsContextParameterizedEntryPoint)
                                     .ToList();

        // One case per refusal an entry point can raise, each carrying the state the collection is in when the call
        // arrives and the argument that makes it refuse. The mixed-context case arrives with another context already
        // bound, so the bind itself refuses. Every span case arrives with NOTHING bound - the one state in which the
        // bind REGISTERS - and names the entry point's own context, so the refusal lands after the bind has already
        // written. Driving a span case from an already-bound collection would make the bind a no-op and assert
        // nothing about what a refusal leaves behind.
        private sealed class RefusalCase
        {
            public RefusalCase(string name,
                               Type boundContextType,
                               Type invokedContextType,
                               Action<EntityFrameworkReliabilityOptions> configuration,
                               Type expectedException)
            {
                Name = name;
                BoundContextType = boundContextType;
                InvokedContextType = invokedContextType;
                Configuration = configuration;
                ExpectedException = expectedException;
            }

            public string Name { get; }

            public Type BoundContextType { get; }

            public Type InvokedContextType { get; }

            public Action<EntityFrameworkReliabilityOptions> Configuration { get; }

            public Type ExpectedException { get; }
        }

        private static RefusalCase SpanRefusal(string name, Action<EntityFrameworkReliabilityOptions> configuration)
            => new RefusalCase(name, null, typeof(PrimaryDbContext), configuration, typeof(ArgumentOutOfRangeException));

        // Both bounds of all three spans. Each is a separate throw site, each sits after the bind, and each is
        // therefore its own chance to leave a caller holding a registration its call never completed.
        private static readonly RefusalCase[] _spanRefusals = new[]
        {
            SpanRefusal("an InboxDeduplicationWindow naming no time",
                        options => options.InboxDeduplicationWindow = TimeSpan.Zero),
            SpanRefusal("an InboxDeduplicationWindow no cutoff can be derived from",
                        options => options.InboxDeduplicationWindow = TimeSpan.MaxValue),
            SpanRefusal("a ProcessedOutboxRetention naming no time",
                        options => options.ProcessedOutboxRetention = TimeSpan.Zero),
            SpanRefusal("a ProcessedOutboxRetention no cutoff can be derived from",
                        options => options.ProcessedOutboxRetention = TimeSpan.MaxValue),
            SpanRefusal("a PurgeInterval naming no time",
                        options => options.PurgeInterval = TimeSpan.Zero),
            SpanRefusal("a PurgeInterval the scheduler cannot schedule",
                        options => options.PurgeInterval = TimeSpan.FromMilliseconds((double)uint.MaxValue))
        };

        private static IEnumerable<RefusalCase> RefusalCasesFor(MethodInfo entryPoint)
        {
            yield return new RefusalCase("a second DbContext",
                                         typeof(PrimaryDbContext),
                                         typeof(SecondaryDbContext),
                                         null,
                                         typeof(InvalidOperationException));

            var takesRetentionConfiguration = entryPoint.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(Action<EntityFrameworkReliabilityOptions>));

            if (!takesRetentionConfiguration)
            {
                yield break;
            }

            foreach (var spanRefusal in _spanRefusals)
            {
                yield return spanRefusal;
            }
        }

        public static TheoryData<string, string> RefusedCalls()
        {
            var refusedCalls = new TheoryData<string, string>();

            foreach (var entryPoint in ContextParameterizedEntryPoints())
            {
                foreach (var refusal in RefusalCasesFor(entryPoint))
                {
                    refusedCalls.Add(entryPoint.Name, refusal.Name);
                }
            }

            return refusedCalls;
        }

        // The sweep: every public TContext-parameterized entry point crossed with every refusal it can raise,
        // watching the caller's own collection across the throw. MustLeaveTheServiceCollectionExactlyAsItWasWhenA
        // CallIsRefused above holds one of those crossings; the refusals it does not reach are the ones raised
        // AFTER an entry point has begun registering, which is invisible both to the door that returns normally
        // and to a caller that catches.
        [Theory]
        [MemberData(nameof(RefusedCalls))]
        public void MustLeaveTheServiceCollectionExactlyAsItWasWhenAnyRefusalIsRaised(string entryPointName, string refusalName)
        {
            var entryPoint = ContextParameterizedEntryPoints().Single(method => method.Name == entryPointName);
            var refusal = RefusalCasesFor(entryPoint).Single(candidate => candidate.Name == refusalName);

            var services = new ServiceCollection();
            List<ServiceDescriptor> beforeTheRefusedCall = null;

            Action register = () => ConfigurePipeline(services, builder =>
            {
                BindContextDirectly(builder.Services, refusal.BoundContextType);
                beforeTheRefusedCall = builder.Services.ToList();
                InvokeWithContext(entryPoint, builder, refusal.InvokedContextType, refusal.Configuration);
            });

            register.Should().Throw<Exception>("'{0}' must refuse {1}", entryPointName, refusalName)
                    .Which.GetType().Should().Be(refusal.ExpectedException);

            beforeTheRefusedCall.Should().NotBeNull();
            services.Should().Equal(beforeTheRefusedCall,
                                    "'{0}' refusing {1} must leave every descriptor in the slot the caller left it in",
                                    entryPointName, refusalName);
        }

        // The binding marker is registered directly rather than through one of the entry points, so a case states
        // the collection's starting state instead of inheriting whatever else a door would have registered with it.
        private static void BindContextDirectly(IServiceCollection services, Type contextType)
        {
            if (contextType is null)
            {
                return;
            }

            services.AddSingleton(new ReliabilityContextBinding(contextType));
        }

        private static bool IsContextParameterizedEntryPoint(MethodInfo method)
        {
            if (!method.IsGenericMethodDefinition)
            {
                return false;
            }

            var typeParameters = method.GetGenericArguments();

            return typeParameters.Length == 1
                && typeParameters[0].GetGenericParameterConstraints().Any(constraint => constraint == typeof(DbContext));
        }

        private static void InvokeWithContext(MethodInfo entryPoint,
                                              CommandPipelineBuilder pipelineBuilder,
                                              Type contextType,
                                              Delegate suppliedConfiguration)
        {
            var closedEntryPoint = entryPoint.MakeGenericMethod(contextType);
            var arguments = closedEntryPoint.GetParameters()
                                            .Select(parameter => BuildArgument(parameter, pipelineBuilder, suppliedConfiguration))
                                            .ToArray();

            try
            {
                closedEntryPoint.Invoke(null, arguments);
            }
            catch (TargetInvocationException invocationFailure)
            {
                ExceptionDispatchInfo.Capture(invocationFailure.InnerException).Throw();
            }
        }

        // Fails loudly on a parameter type it has no argument for. Skipping one would leave the entry point
        // unexercised while this sweep still reported success.
        private static object BuildArgument(ParameterInfo parameter, CommandPipelineBuilder pipelineBuilder, Delegate suppliedConfiguration)
        {
            if (parameter.ParameterType == typeof(CommandPipelineBuilder))
            {
                return pipelineBuilder;
            }

            if (parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Action<>))
            {
                return suppliedConfiguration is null
                    ? BuildNoOpConfiguration(parameter.ParameterType)
                    : RequireConfigurationFor(parameter, suppliedConfiguration);
            }

            throw new NotSupportedException(
                $"'{nameof(WhenBindingReliabilityContext)}' has no argument for parameter '{parameter.Name}' of type " +
                $"'{parameter.ParameterType}' on '{parameter.Member.Name}'. Supply one so the entry point is driven.");
        }

        private static Delegate BuildNoOpConfiguration(Type configurationType)
        {
            var noOp = typeof(WhenBindingReliabilityContext)
                .GetMethod(nameof(IgnoreConfiguration), BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(configurationType.GetGenericArguments());

            return noOp.CreateDelegate(configurationType);
        }

        // A configuration a case supplied that this entry point cannot take would otherwise fall back to the no-op,
        // leaving the refusal that case exists to raise unraised while the sweep still reported success.
        private static Delegate RequireConfigurationFor(ParameterInfo parameter, Delegate suppliedConfiguration)
        {
            if (!parameter.ParameterType.IsInstanceOfType(suppliedConfiguration))
            {
                throw new NotSupportedException(
                    $"'{nameof(WhenBindingReliabilityContext)}' supplied a '{suppliedConfiguration.GetType()}' for parameter " +
                    $"'{parameter.Name}' of type '{parameter.ParameterType}' on '{parameter.Member.Name}'.");
            }

            return suppliedConfiguration;
        }

        private static void IgnoreConfiguration<TOptions>(TOptions options)
        {
        }

        private sealed class TestCommand : ICommand
        {
        }

        private sealed class PrimaryDbContext : DbContext
        {
            public PrimaryDbContext(DbContextOptions options) : base(options) { }
        }

        private sealed class SecondaryDbContext : DbContext
        {
            public SecondaryDbContext(DbContextOptions options) : base(options) { }
        }
    }
}
