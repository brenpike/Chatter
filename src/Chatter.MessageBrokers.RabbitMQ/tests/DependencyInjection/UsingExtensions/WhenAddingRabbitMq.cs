using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.DependencyInjection.UsingExtensions
{
    // Pins the OBSERVABLE wiring contract of AddRabbitMq at the IServiceCollection descriptor level (the SSB
    // approach): which service types are registered, at which lifetimes, that IMessagingInfrastructure is a
    // Singleton factory descriptor, and that FullAtomicityViaInfrastructure is rejected at registration.
    // AddRabbitMq runs against a BARE ChatterBuilder (no AddChatterCqrs/AddMessageBrokers, so no
    // AssemblySourceFilter.Apply() AppDomain scan), mirroring WhenAddingSqlServiceBroker.
    public class WhenAddingRabbitMq : Testing.Core.Context
    {
        // The Chatter.CQRS assembly contains no [BrokeredMessage]-decorated IMessage types, so scoping the core
        // assembly scan to it makes attribute-driven receiver discovery deterministically empty.
        private static readonly System.Reflection.Assembly NoBrokeredMessageAssembly = typeof(Chatter.CQRS.IMessage).Assembly;

        private static IConfiguration EmptyConfig()
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        private static IServiceCollection BuildRegistration(Action<IServiceCollection> preconfigure = null)
        {
            var services = new ServiceCollection();
            preconfigure?.Invoke(services);
            var filter = AssemblySourceFilterBuilder.New().Build();
            var builder = ChatterBuilder.Create(services, EmptyConfig(), filter);

            builder.AddRabbitMq(o => o.AddRabbitMqOptions(hostName: "localhost"));

            return services;
        }

        // Runs AddRabbitMq behind the REAL core registration path (AddChatterCqrs -> AddMessageBrokers), so the
        // MessageBrokerOptions instance RejectFullAtomicity reads off the descriptor set is the one the core
        // PUBLISHED, published in the order production publishes it. Assembly scanning is scoped to the Chatter.CQRS
        // assembly, which carries no [BrokeredMessage]-decorated types, so receiver discovery is deterministically
        // empty and the multiple-RabbitMQ-receiver guard cannot fire.
        private static IServiceCollection BuildRegistrationOverRealCore(
            Action<MessageBrokerOptionsBuilder> configureMessageBrokers)
        {
            var services = new ServiceCollection();

            services.AddChatterCqrs(EmptyConfig(), NoBrokeredMessageAssembly)
                    .AddMessageBrokers(
                        optionsBuilder: configureMessageBrokers,
                        receiverHandlerSourceBuilder: b => b.WithExplicitAssemblies(NoBrokeredMessageAssembly))
                    .AddRabbitMq(o => o.AddRabbitMqOptions(hostName: "localhost"));

            return services;
        }

        // Mirrors the descriptor lookup RejectFullAtomicity performs (ServiceType == typeof(MessageBrokerOptions),
        // ImplementationInstance narrowed with `as`). Returns null on exactly the misses the production read
        // swallows: no descriptor, or a descriptor that is not an ImplementationInstance.
        private static MessageBrokerOptions ReadGlobalOptionsOffDescriptors(IServiceCollection services)
            => services.FirstOrDefault(d => d.ServiceType == typeof(MessageBrokerOptions))?
                       .ImplementationInstance as MessageBrokerOptions;

        private static ServiceDescriptor Single(IServiceCollection services, Type serviceType)
            => services.Single(d => d.ServiceType == serviceType);

        private static Type ConnectionSourceType()
            => typeof(RabbitMqMessageContext).Assembly.GetType(
                "Chatter.MessageBrokers.RabbitMQ.Receiving.IRabbitMqConnectionSource", throwOnError: true);

        private static Type ReceiverType()
            => typeof(RabbitMqMessageContext).Assembly.GetType(
                "Chatter.MessageBrokers.RabbitMQ.Receiving.RabbitMqReceiver", throwOnError: true);

        [Fact]
        public void MustRegisterMessagingInfrastructureAsSingletonViaFactory()
        {
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(IMessagingInfrastructure));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationFactory.Should().NotBeNull();
            descriptor.ImplementationInstance.Should().BeNull();
            descriptor.ImplementationType.Should().BeNull();
        }

        // INVARIANT: the one deliberate lifetime divergence from the SSB fold — the AMQP connection source is a
        // process SINGLETON (SSB's connection source is Scoped), because one IConnection is owned per process.
        [Fact]
        public void MustRegisterConnectionSourceAsSingleton()
        {
            var services = BuildRegistration();

            Single(services, ConnectionSourceType()).Lifetime.Should().Be(ServiceLifetime.Singleton);
        }

        // INVARIANT: the CONTAINER owns the connection source. AddRabbitMq registers it by SERVICE TYPE plus
        // IMPLEMENTATION TYPE, so MSDI constructs the instance and the root provider therefore disposes it at process
        // shutdown — that is what releases the AMQP connection. No consumer has to escalate its own Dispose to the
        // singleton to get the connection closed. RabbitMqConnectionSource implements both IDisposable and
        // IAsyncDisposable, so the sync and async container teardown paths both reach it. The real source is used
        // deliberately (not a spy) because the claim under test is the REAL registration's ownership; it is never
        // connected, so disposal performs no broker I/O.
        [Fact]
        public async Task MustDisposeContainerCreatedConnectionSourceWithRootProvider()
        {
            var services = BuildRegistration();

            // The registration SHAPE is what makes the container the owner: a refactor to an ImplementationInstance
            // registration would hand ownership back to the caller and the root provider would stop disposing it.
            var descriptor = Single(services, ConnectionSourceType());
            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationType.Should().Be<RabbitMqConnectionSource>();
            descriptor.ImplementationInstance.Should().BeNull();

            var provider = services.BuildServiceProvider();
            var source = provider.GetRequiredService<IRabbitMqConnectionSource>();
            source.Should().BeOfType<RabbitMqConnectionSource>();

            provider.Dispose();

            Func<Task> acquirePublishChannel = () => source.AcquirePublishChannelAsync(CancellationToken.None);
            await acquirePublishChannel.Should().ThrowAsync<ObjectDisposedException>(
                "the root provider disposes the source it created, so the AMQP connection is released at process "
                + "shutdown without any consumer escalating a dispose to the singleton");
        }

        [Fact]
        public void MustRegisterSenderAsScoped()
        {
            var services = BuildRegistration();

            var senderType = typeof(RabbitMqMessageContext).Assembly.GetType(
                "Chatter.MessageBrokers.RabbitMQ.Sending.RabbitMqSender", throwOnError: true);

            Single(services, senderType).Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterPredicateProvidersAsSingleton()
        {
            var services = BuildRegistration();

            Single(services, typeof(ICircuitBreakerExceptionPredicatesProvider))
                .Lifetime.Should().Be(ServiceLifetime.Singleton);
            Single(services, typeof(IRetryExceptionPredicatesProvider))
                .Lifetime.Should().Be(ServiceLifetime.Singleton);
        }

        [Fact]
        public void MustRegisterPathBuilderAsSingleton()
        {
            var services = BuildRegistration();

            var pathBuilderType = typeof(RabbitMqMessageContext).Assembly.GetType(
                "Chatter.MessageBrokers.RabbitMQ.RabbitMqPathBuilder", throwOnError: true);

            Single(services, pathBuilderType).Lifetime.Should().Be(ServiceLifetime.Singleton);
        }

        // The core IBodyConverterFactory is a SINGLETON that captures every IBrokeredMessageBodyConverter provider, so
        // a shorter-lived converter would throw "Cannot consume scoped service" under scope validation.
        [Fact]
        public void MustRegisterBodyConverterAsSingleton()
        {
            var services = BuildRegistration();

            Single(services, typeof(IBrokeredMessageBodyConverter))
                .Lifetime.Should().Be(ServiceLifetime.Singleton);
        }

        private static ServiceProvider BuildScopeValidatingProviderOverRealCore()
            => BuildRegistrationOverRealCore(null)
                .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        [Fact]
        public void MustResolveRabbitMqBodyConverterFromRootProviderUnderScopeValidation()
        {
            using var provider = BuildScopeValidatingProviderOverRealCore();
            var contentType = new RabbitMqBodyConverter().ContentType;

            var converter = provider.GetRequiredService<IBodyConverterFactory>().CreateBodyConverter(contentType);

            converter.Should().BeOfType<RabbitMqBodyConverter>();
        }

        [Fact]
        public void MustResolveSameRabbitMqBodyConverterAcrossScopesUnderScopeValidation()
        {
            using var provider = BuildScopeValidatingProviderOverRealCore();
            using var sendingScope = provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
            using var receivingScope = provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var contentType = new RabbitMqBodyConverter().ContentType;

            var sendingConverter = sendingScope.ServiceProvider.GetRequiredService<IBodyConverterFactory>().CreateBodyConverter(contentType);
            var receivingConverter = receivingScope.ServiceProvider.GetRequiredService<IBodyConverterFactory>().CreateBodyConverter(contentType);

            sendingConverter.Should().BeOfType<RabbitMqBodyConverter>();
            sendingConverter.Should().BeSameAs(receivingConverter);
        }

        // The RabbitMqBodyConverter is registered as an IBrokeredMessageBodyConverter PROVIDER so the core
        // BodyConverterFactory enumerates it and keys it under its ContentType — that is what lets the sender and
        // receiver resolve it through IBodyConverterFactory keyed on RabbitMqOptions.MessageBodyType.
        [Fact]
        public void MustRegisterRabbitMqBodyConverterAsTheBodyConverterProvider()
        {
            var services = BuildRegistration();

            Single(services, typeof(IBrokeredMessageBodyConverter))
                .ImplementationType.Should().Be<RabbitMqBodyConverter>();
        }

        // The core BodyConverterFactory built over the registered provider resolves a JSON-capable converter for the
        // default MessageBodyType ("application/json; charset=utf-8") — the RabbitMqBodyConverter keyed under its
        // own ContentType — confirming the option selects a real converter rather than being ignored.
        [Fact]
        public void MustResolveJsonCapableConverterForDefaultBodyTypeViaFactory()
        {
            var factory = new Chatter.MessageBrokers.BodyConverterFactory(new IBrokeredMessageBodyConverter[]
            {
                new RabbitMqBodyConverter(),
                new JsonBodyConverter()
            });

            var converter = factory.CreateBodyConverter("application/json; charset=utf-8");

            converter.Should().BeOfType<RabbitMqBodyConverter>();
            converter.ContentType.Should().Be("application/json; charset=utf-8");
        }

        [Fact]
        public void MustRegisterRabbitMqOptionsAsSingletonInstance()
        {
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(Chatter.MessageBrokers.RabbitMQ.Configuration.RabbitMqOptions));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationInstance.Should()
                .BeOfType<Chatter.MessageBrokers.RabbitMQ.Configuration.RabbitMqOptions>()
                .Which.HostName.Should().Be("localhost");
        }

        // --- FullAtomicityViaInfrastructure rejection at registration -----------------------------------

        // MessageBrokerOptions.TransactionMode has an internal setter (set by core configuration); set it via
        // reflection to model a host that configured the global mode to FullAtomicityViaInfrastructure.
        private static MessageBrokerOptions GlobalOptions(TransactionMode mode)
        {
            var options = new MessageBrokerOptions();
            typeof(MessageBrokerOptions).GetProperty(nameof(MessageBrokerOptions.TransactionMode))
                .SetValue(options, mode);
            return options;
        }

        [Fact]
        public void MustThrowWhenGlobalTransactionModeIsFullAtomicity()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton(GlobalOptions(TransactionMode.FullAtomicityViaInfrastructure)));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void MustThrowWhenAnAttributedRabbitMqReceiverRequestsFullAtomicity()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(new ReceiverOptions
                    {
                        InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                        TransactionMode = TransactionMode.FullAtomicityViaInfrastructure
                    })));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void MustNotThrowWhenReceiverRequestsReceiveOnly()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(new ReceiverOptions
                    {
                        InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                        TransactionMode = TransactionMode.ReceiveOnly
                    })));

            act.Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowWhenGlobalTransactionModeIsNone()
        {
            IServiceCollection services = null;
            Action act = () => services = BuildRegistration(
                s => s.AddSingleton(GlobalOptions(TransactionMode.None)));

            act.Should().NotThrow();

            // "Did not throw" alone is ambiguous: the production read narrows ImplementationInstance with `as`, so a
            // MISS yields null and the guard silently passes. Assert the read SUCCEEDED — the descriptor was found
            // and carried the mode under test — so this arm cannot go green on a read that found nothing.
            var globalOptions = ReadGlobalOptionsOffDescriptors(services);
            globalOptions.Should().NotBeNull();
            globalOptions.TransactionMode.Should().Be(TransactionMode.None);
        }

        // --- the read is against the instance the CORE published ---------------------------------------

        [Fact]
        public void MustRejectFullAtomicityPublishedByTheCoreOptionsBuild()
        {
            // The core publishes MessageBrokerOptions as a singleton ImplementationInstance during
            // AddMessageBrokers, and AddRabbitMq reads it straight off the descriptor set afterwards. Driving the
            // mode through the REAL core builder pins that ORDERING: were the publish ever to move after this read,
            // the `as` cast would yield null, the guard would not fire, and an unsupportable configuration would
            // boot silently.
            Action act = () => BuildRegistrationOverRealCore(
                mb => mb.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void MustReadTheFinalizedGlobalTransactionModeOffTheCorePublishedInstance()
        {
            var services = BuildRegistrationOverRealCore(mb => mb.WithTransactionMode(TransactionMode.ReceiveOnly));

            // The same descriptor lookup RejectFullAtomicity performs, against the core's own publish: a non-null
            // instance carrying the finalized mode, and the very instance consumers resolve — not a second object.
            var globalOptions = ReadGlobalOptionsOffDescriptors(services);
            globalOptions.Should().NotBeNull();
            globalOptions.TransactionMode.Should().Be(TransactionMode.ReceiveOnly);

            using var provider = services.BuildServiceProvider();
            globalOptions.Should().BeSameAs(provider.GetRequiredService<MessageBrokerOptions>());
        }

        // A non-RabbitMQ receiver requesting FullAtomicity must NOT be claimed by AddRabbitMq when RabbitMQ is
        // not the resolved default (it cannot be — an IMessagingInfrastructure descriptor already exists).
        [Fact]
        public void MustNotThrowWhenAtomicReceiverBelongsToAnotherInfrastructure()
        {
            Action act = () => BuildRegistration(services =>
            {
                services.AddSingleton<IMessagingInfrastructure>(new ForeignMessagingInfrastructure());
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(new ReceiverOptions
                    {
                        InfrastructureType = "Chatter.Infrastructure.SomeOtherBroker",
                        TransactionMode = TransactionMode.FullAtomicityViaInfrastructure
                    }));
            });

            act.Should().NotThrow();
        }

        // --- Multiple-RabbitMQ-receiver rejection at registration --------------------------------------

        // Two RabbitMQ-attributed receivers must throw at registration: the singleton connection source owns one
        // receive channel and one consumer registration, so a second receiver would clobber the first.
        [Fact]
        public void MustThrowWhenMoreThanOneRabbitMqReceiverIsDiscovered()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(
                        new ReceiverOptions
                        {
                            InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                            TransactionMode = TransactionMode.ReceiveOnly
                        },
                        new ReceiverOptions
                        {
                            InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                            TransactionMode = TransactionMode.ReceiveOnly
                        })));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void MustNotThrowWhenExactlyOneRabbitMqReceiverIsDiscovered()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(new ReceiverOptions
                    {
                        InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                        TransactionMode = TransactionMode.ReceiveOnly
                    })));

            act.Should().NotThrow();
        }

        // Receivers belonging to ANOTHER infrastructure must NOT count toward the single-RabbitMQ-receiver limit. A
        // ForeignMessagingInfrastructure descriptor exists, so RabbitMQ is not the resolved default and the two
        // foreign receivers are not claimed: only the one RabbitMQ receiver counts, so no throw.
        [Fact]
        public void MustNotThrowWhenAdditionalReceiversBelongToAnotherInfrastructure()
        {
            Action act = () => BuildRegistration(services =>
            {
                services.AddSingleton<IMessagingInfrastructure>(new ForeignMessagingInfrastructure());
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(
                        new ReceiverOptions
                        {
                            InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                            TransactionMode = TransactionMode.ReceiveOnly
                        },
                        new ReceiverOptions
                        {
                            InfrastructureType = "Chatter.Infrastructure.SomeOtherBroker",
                            TransactionMode = TransactionMode.ReceiveOnly
                        },
                        new ReceiverOptions
                        {
                            InfrastructureType = "Chatter.Infrastructure.SomeOtherBroker",
                            TransactionMode = TransactionMode.ReceiveOnly
                        }));
            });

            act.Should().NotThrow();
        }

        // --- Hosted-receiver factory must NOT dispose the singleton source at factory return -------------

        // REGRESSION (codex P1, PR #194): the IMessagingInfrastructure receiver factory delegate must NOT
        // open-resolve-and-DISPOSE a transient scope per Create() call. The core drives the returned receiver
        // through InitializeAsync, then StopReceivingAsync, then Dispose — all AFTER the delegate returns — so a
        // per-call `using var scope` would dispose the receiver at factory return and hand the core an
        // already-disposed receiver. The spy below records disposal of the SINGLETON IRabbitMqConnectionSource so
        // these tests also pin that nothing on the receive-infrastructure path tears down the process-wide source:
        // the container created the production source and the root provider is what disposes it, at shutdown.
        private static IServiceProvider BuildProviderWithSpyConnectionSource(out SpyRabbitMqConnectionSource spy)
        {
            var services = new ServiceCollection();
            var filter = AssemblySourceFilterBuilder.New().Build();
            var builder = ChatterBuilder.Create(services, EmptyConfig(), filter);
            builder.AddRabbitMq(o => o.AddRabbitMqOptions(hostName: "localhost"));

            // Swap the production singleton source for a disposal-recording spy (still a SINGLETON + IDisposable,
            // so any dispose reaching the process-wide source on the receiver-resolution path is recorded).
            var capturedSpy = new SpyRabbitMqConnectionSource();
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IRabbitMqConnectionSource))
                {
                    services.RemoveAt(i);
                }
            }
            services.AddSingleton<IRabbitMqConnectionSource>(capturedSpy);
            // The receiver is constructed by the infrastructure factory delegate via ActivatorUtilities, which
            // resolves its IBodyConverterFactory and logger from the ROOT provider — both must therefore exist.
            services.AddSingleton<IBodyConverterFactory>(
                new BodyConverterFactory(new IBrokeredMessageBodyConverter[] { new RabbitMqBodyConverter(), new JsonBodyConverter() }));
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

            spy = capturedSpy;
            return services.BuildServiceProvider();
        }

        [Fact]
        public void MustNotDisposeSingletonConnectionSourceWhenResolvingReceiveInfrastructure()
        {
            var provider = BuildProviderWithSpyConnectionSource(out var spy);

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();
            var receiver = infrastructure.ReceiveInfrastructure;

            receiver.Should().NotBeNull();
            spy.DisposeCount.Should().Be(0, "the receiver factory must hand the core a live receiver, and nothing "
                + "on that path may tear down the shared singleton connection source before the core drives "
                + "InitializeAsync");
        }

        [Fact]
        public void MustResolveReceiveInfrastructureRepeatedlyWithoutDisposingTheSource()
        {
            var provider = BuildProviderWithSpyConnectionSource(out var spy);

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();
            _ = infrastructure.ReceiveInfrastructure;
            _ = infrastructure.ReceiveInfrastructure;

            spy.DisposeCount.Should().Be(0);
        }

        // --- the receiver is NOT a container-published service ------------------------------------------

        // ROOT (issue #367): AddRabbitMq must publish NO descriptor for RabbitMqReceiver. The receiver's only
        // legitimate consumer is the IMessagingInfrastructure factory delegate, which constructs the one instance
        // itself; publishing the concrete type additionally created the category "a receiver instance nothing ever
        // initialized, held by an arbitrary scope" — the category every per-instance latch check was patching.
        [Fact]
        public void MustNotRegisterTheReceiverInTheContainer()
        {
            var services = BuildRegistration();

            services.Should().NotContain(
                d => d.ServiceType == ReceiverType(),
                "the receiver is constructed at its single call site, so no descriptor may offer it to anyone else");
        }

        // CLOSURE PROOF for the same root: with no descriptor, a consumer scope cannot obtain a receiver at all —
        // neither the silent GetService nor the throwing GetRequiredService. A stray scope that cannot hold a
        // receiver cannot dispose one, so no receiver dispose can ever reach the shared singleton source by that
        // route. This supersedes the weaker "a stray scope's receiver must not dispose the source" guard.
        [Fact]
        public void MustNotResolveTheReceiverFromAConsumerScope()
        {
            var provider = BuildProviderWithSpyConnectionSource(out _);

            using var consumerScope = provider.GetRequiredService<IServiceScopeFactory>().CreateScope();

            consumerScope.ServiceProvider.GetService(ReceiverType()).Should().BeNull();
            Action resolve = () => consumerScope.ServiceProvider.GetRequiredService(ReceiverType());
            resolve.Should().Throw<InvalidOperationException>(
                "no consumer scope may obtain a receiver instance — that category no longer exists");
        }

        // The core reads IMessagingInfrastructure.ReceiveInfrastructure — a property that calls the factory's
        // Create() on EVERY access — so "constructed once at a single site" only holds if every Create returns the
        // SAME instance. A refactor to ActivatorUtilities-per-call would re-mint the very category deleted above:
        // instances nobody initialized, each of them disposable, each reachable by whoever touched the property.
        [Fact]
        public void MustHandTheCoreTheSameReceiverInstanceOnEveryCreate()
        {
            var provider = BuildProviderWithSpyConnectionSource(out _);
            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            var first = infrastructure.ReceiveInfrastructure;
            var second = infrastructure.ReceiveInfrastructure;

            first.Should().BeSameAs(second, "exactly one receiver may exist in the process");
        }

        // Constructing the receiver at the singleton infrastructure's own composition site only works because EVERY
        // RabbitMqReceiver constructor dependency — IRabbitMqConnectionSource, RabbitMqOptions, IBodyConverterFactory
        // and ILogger<> — is a SINGLETON. Were one of them ever made Scoped, ValidateScopes would refuse the root
        // resolution here rather than letting a captured-dependency defect reach a host.
        [Fact]
        public void MustConstructTheReceiverFromTheRootProviderUnderScopeValidation()
        {
            var services = BuildRegistrationOverRealCore(null);
            // The bare core registration carries no logging; a real host's ILogger<> is likewise an open-generic
            // singleton, which is what the sender already requires to dispatch at all.
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            provider.GetRequiredService<IMessagingInfrastructure>().ReceiveInfrastructure.Should().NotBeNull();
        }

        // A disposal-recording IRabbitMqConnectionSource spy. Implements BOTH IDisposable (the sync container
        // teardown path) and IAsyncDisposable, matching the production source, so any dispose reaching the
        // process-wide source on the receive-infrastructure path is recorded.
        private sealed class SpyRabbitMqConnectionSource : IRabbitMqConnectionSource, IDisposable
        {
            public int DisposeCount { get; private set; }

            public long CurrentReceiveChannelEpoch => 0;

            public void Dispose() => DisposeCount++;

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                return default;
            }

            public Task StartReceivingAsync(Func<IChannel, long, CancellationToken, Task<string>> registerConsumer,
                                            CancellationToken cancellationToken) => throw new NotImplementedException();

            public Task StopReceivingAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

            public Task<TResult> RunOnReceiveChannelAsync<TResult>(Func<IChannel, long, Task<TResult>> operation,
                                                                   CancellationToken cancellationToken)
                => throw new NotImplementedException();

            public Task<RabbitMqPublishChannelRental> AcquirePublishChannelAsync(CancellationToken cancellationToken)
                => throw new NotImplementedException();
        }

        private sealed class StubDiscoveredReceiverRegistry : IDiscoveredReceiverRegistry
        {
            private readonly List<ReceiverOptions> _receivers = new List<ReceiverOptions>();

            public StubDiscoveredReceiverRegistry(params ReceiverOptions[] receivers)
                => _receivers.AddRange(receivers);

            public void Register(ReceiverOptions options) => _receivers.Add(options);
            public IReadOnlyCollection<ReceiverOptions> DiscoveredReceivers => _receivers;
        }

        // A stand-in IMessagingInfrastructure so RabbitMQ is NOT the resolved default in the foreign-receiver
        // test (the default is "first-registered" and is decided by descriptor presence, not resolution).
        private sealed class ForeignMessagingInfrastructure : IMessagingInfrastructure
        {
            public string Type => "Chatter.Infrastructure.SomeOtherBroker";
            public IMessagingInfrastructureReceiver ReceiveInfrastructure => throw new NotImplementedException();
            public Chatter.MessageBrokers.Sending.IMessagingInfrastructureDispatcher DispatchInfrastructure => throw new NotImplementedException();
            public IBrokeredMessagePathBuilder PathBuilder => throw new NotImplementedException();
        }
    }
}
