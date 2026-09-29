using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.DependencyInjection.UsingExtensions
{
    // Characterization tests pinning the OBSERVABLE wiring contract of AddSqlServiceBroker: the
    // ServiceDescriptor shape that the production registration leaves on the IServiceCollection — the
    // service types, lifetimes, and the presence of an IMessagingInfrastructure factory descriptor.
    //
    // These assert the wiring contract (which service types are registered, at which lifetimes, and that
    // IMessagingInfrastructure is produced through a Singleton factory) — NOT the name of the concrete
    // infrastructure factory type. They therefore survive the STEP-006 rewire from the per-broker
    // SqlServiceBrokerInfrastructureFactory to the shared core factory unchanged.
    //
    // SCOPE (descriptor-shape, not full resolution): these tests deliberately pin the wiring at the
    // IServiceCollection descriptor level — against the real, unmodified AddSqlServiceBroker output, which
    // performs no AppDomain scan on its own — rather than resolving IMessagingInfrastructure end-to-end
    // through AddChatterCqrs / AddMessageBrokers.
    //
    // HISTORICAL NOTE: this descriptor-shape approach originally existed because full bootstrapping was not
    // reachable in the SSB test AppDomain. Both AddChatterCqrs and AddMessageBrokers call
    // AssemblySourceFilter.Apply(), which enumerates AppDomain.CurrentDomain.GetAssemblies() and calls
    // GetTypes() on every loaded assembly; the legacy System.Data.SqlClient 4.6.x assembly (transitively
    // referenced by this module at the time) threw ReflectionTypeLoadException ("Could not load type
    // 'SqlGuidCaster' ... incorrectly aligned or overlapped") on that scan. The migration to
    // Microsoft.Data.SqlClient (#204) removed System.Data.SqlClient from this module's dependency graph, so
    // that specific crash no longer blocks the scan. A full-resolution DI test that exercises the real
    // Chatter bootstrap path is now worth adding (tracked separately); these descriptor-shape characterization
    // tests are retained as the always-available wiring-contract pin regardless of AppDomain scan behavior.
    public class WhenAddingSqlServiceBroker : Testing.Core.Context
    {
        private const string _connectionString =
            "Server=test;Database=test;Trusted_Connection=True;";

        private static IConfiguration EmptyConfig()
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        // Runs the real AddSqlServiceBroker against a bare ChatterBuilder (no AddChatterCqrs / AddMessageBrokers,
        // so no AssemblySourceFilter.Apply() AppDomain scan) and returns the resulting service collection. The
        // AddSqlServiceBrokerOptions(connectionString) overload is used because WithConnectionString alone
        // assumes a previously-constructed options object — the string overload constructs it.
        private static IServiceCollection BuildRegistration()
        {
            var services = new ServiceCollection();
            var filter = AssemblySourceFilterBuilder.New().Build();
            var builder = ChatterBuilder.Create(services, EmptyConfig(), filter);

            builder.AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(_connectionString));

            return services;
        }

        // Runs AddSqlServiceBroker behind the REAL core registration path (AddChatterCqrs -> AddMessageBrokers), which
        // registers the singleton IBodyConverterFactory and the core converters, and builds a scope-validating
        // provider as a host does. Assembly scanning is scoped to the Chatter.CQRS assembly, which carries no
        // [BrokeredMessage]-decorated types, so receiver discovery is deterministically empty.
        private static ServiceProvider BuildScopeValidatingProviderOverRealCore()
        {
            var services = new ServiceCollection();
            var noBrokeredMessageAssembly = typeof(Chatter.CQRS.IMessage).Assembly;

            services.AddChatterCqrs(EmptyConfig(), noBrokeredMessageAssembly)
                    .AddMessageBrokers(
                        optionsBuilder: null,
                        receiverHandlerSourceBuilder: b => b.WithExplicitAssemblies(noBrokeredMessageAssembly))
                    .AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(_connectionString));

            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        private static ServiceDescriptor Single(IServiceCollection services, Type serviceType)
            => services.Single(d => d.ServiceType == serviceType);

        [Fact]
        public void MustRegisterBodyConverterAsSingleton()
        {
            // The core IBodyConverterFactory is a SINGLETON that captures every IBrokeredMessageBodyConverter
            // provider, so a shorter-lived converter would throw "Cannot consume scoped service" under scope validation.
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(IBrokeredMessageBodyConverter));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationType.Should().Be<JsonUnicodeBodyConverter>();
        }

        [Fact]
        public void MustResolveJsonUnicodeBodyConverterFromRootProviderUnderScopeValidation()
        {
            using var provider = BuildScopeValidatingProviderOverRealCore();
            var contentType = new JsonUnicodeBodyConverter().ContentType;

            var converter = provider.GetRequiredService<IBodyConverterFactory>().CreateBodyConverter(contentType);

            converter.Should().BeOfType<JsonUnicodeBodyConverter>();
        }

        [Fact]
        public void MustResolveSameJsonUnicodeBodyConverterAcrossScopesUnderScopeValidation()
        {
            using var provider = BuildScopeValidatingProviderOverRealCore();
            using var sendingScope = provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
            using var receivingScope = provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var contentType = new JsonUnicodeBodyConverter().ContentType;

            var sendingConverter = sendingScope.ServiceProvider.GetRequiredService<IBodyConverterFactory>().CreateBodyConverter(contentType);
            var receivingConverter = receivingScope.ServiceProvider.GetRequiredService<IBodyConverterFactory>().CreateBodyConverter(contentType);

            sendingConverter.Should().BeOfType<JsonUnicodeBodyConverter>();
            sendingConverter.Should().BeSameAs(receivingConverter);
        }

        [Fact]
        public void MustRegisterMessagingInfrastructureAsSingletonViaFactory()
        {
            // Pins the IMessagingInfrastructure descriptor: a single Singleton registration produced by a
            // factory delegate (the lambda that builds MessagingInfrastructure from the resolved
            // infrastructure factory). Asserting the factory PRESENCE — not the concrete factory type —
            // survives the STEP-006 rewire.
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(IMessagingInfrastructure));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationFactory.Should().NotBeNull();
            descriptor.ImplementationInstance.Should().BeNull();
            descriptor.ImplementationType.Should().BeNull();
        }

        [Fact]
        public void MustRegisterSqlConnectionSourceAsScoped()
        {
            // The connection source is Scoped because it is injected into the Scoped receiver and sender.
            // ISqlConnectionSource is internal; this test assembly has InternalsVisibleTo, so the closed
            // service type is referenceable. Pinned via reflection-free descriptor lookup over the type.
            var services = BuildRegistration();

            var connectionSourceType =
                typeof(SSBMessageContext).Assembly.GetType(
                    "Chatter.MessageBrokers.SqlServiceBroker.Receiving.ISqlConnectionSource", throwOnError: true);

            var descriptor = Single(services, connectionSourceType);

            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterReceiverAndSenderAsScoped()
        {
            // The infrastructure factory delegates open a fresh DI scope per Create and resolve these Scoped
            // services, so the receiver and sender must be registered Scoped for the fresh-scope-per-Create
            // behavior to hold. Both types are internal; resolved by name through the IVT'd assembly.
            var services = BuildRegistration();

            var brokerAssembly = typeof(SSBMessageContext).Assembly;
            var receiverType = brokerAssembly.GetType(
                "Chatter.MessageBrokers.SqlServiceBroker.Receiving.SqlServiceBrokerReceiver", throwOnError: true);
            var senderType = brokerAssembly.GetType(
                "Chatter.MessageBrokers.SqlServiceBroker.Sending.SqlServiceBrokerSender", throwOnError: true);

            Single(services, receiverType).Lifetime.Should().Be(ServiceLifetime.Scoped);
            Single(services, senderType).Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterSqlServiceBrokerOptionsAsSingletonInstance()
        {
            // The built SqlServiceBrokerOptions are registered as a singleton instance carrying the supplied
            // connection string — the value the receiver/sender materialize into a live connection at runtime.
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(SqlServiceBrokerOptions));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationInstance.Should().BeOfType<SqlServiceBrokerOptions>()
                      .Which.ConnectionString.Should().Be(_connectionString);
        }

        [Fact]
        public void MustNotRegisterCustomPathBuilder()
        {
            // Pins the documented behavior-preservation divergence from ASB: SqlServiceBroker registers NO
            // broker-specific IBrokeredMessagePathBuilder. The MessagingInfrastructure 3-arg ctor therefore
            // falls back to the core default DefaultBrokeredMessagePathBuilder. (ASB instead registers its own
            // AzureServiceBusEntityPathBuilder and uses the 4-arg ctor.) This assertion survives the STEP-006
            // rewire because the 3-arg ctor path is preserved.
            var services = BuildRegistration();

            services.Should().NotContain(d => d.ServiceType == typeof(IBrokeredMessagePathBuilder));
        }

        private const string _alphaQueue = "alpha-queue";
        private const string _betaQueue = "beta-queue";
        private const string _divergentConnectionString =
            "Server=other;Database=other;Trusted_Connection=True;";

        public sealed class AlphaCommand : ICommand { }

        public sealed class BetaCommand : ICommand { }

        [BrokeredMessage("decorated-sending-path")]
        public sealed class DecoratedCommand : ICommand { }

        private static IChatterBuilder NewBareBuilder(IServiceCollection services)
            => ChatterBuilder.Create(services, EmptyConfig(), AssemblySourceFilterBuilder.New().Build());

        private static IDiscoveredReceiverRegistry EffectiveDiscoveredReceiverRegistry(IServiceCollection services)
            => services.LastOrDefault(d => d.ServiceType == typeof(IDiscoveredReceiverRegistry))?
                       .ImplementationInstance as IDiscoveredReceiverRegistry;

        // Registers Alpha on the fixture connection string, then returns a delegate that registers Beta on the
        // connection string it is given, so each fact varies only the second call's transport configuration.
        private static Action RegisterBetaAfterAlpha(IServiceCollection services, string betaConnectionString)
        {
            var builder = NewBareBuilder(services);
            builder.AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(_connectionString)
                                              .AddQueueReceiver<AlphaCommand>(_alphaQueue));
            return () => builder.AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(betaConnectionString)
                                                           .AddQueueReceiver<BetaCommand>(_betaQueue));
        }

        [Fact]
        public void MustRegisterOneSqlServiceBrokerOptionsWhenASecondCallConfiguresEquivalentOptions()
        {
            var services = new ServiceCollection();

            RegisterBetaAfterAlpha(services, new string(_connectionString.AsSpan()))();

            services.Count(d => d.ServiceType == typeof(SqlServiceBrokerOptions)).Should().Be(1);
        }

        [Fact]
        public void MustRegisterTheReceiversOfEveryCallWhenASecondCallConfiguresEquivalentOptions()
        {
            var services = new ServiceCollection();

            RegisterBetaAfterAlpha(services, new string(_connectionString.AsSpan()))();

            EffectiveDiscoveredReceiverRegistry(services).DiscoveredReceivers
                .Select(receiver => receiver.MessageReceiverPath)
                .Should().Equal(_alphaQueue, _betaQueue);
            services.Should().Contain(d => d.ServiceType == typeof(IBrokeredMessageReceiver<AlphaCommand>));
            services.Should().Contain(d => d.ServiceType == typeof(IBrokeredMessageReceiver<BetaCommand>));
        }

        [Fact]
        public void MustRegisterTheReceiverOfASingleCall()
        {
            var services = new ServiceCollection();

            NewBareBuilder(services).AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(_connectionString)
                                                               .AddQueueReceiver<AlphaCommand>(_alphaQueue));

            EffectiveDiscoveredReceiverRegistry(services).DiscoveredReceivers
                .Select(receiver => receiver.MessageReceiverPath)
                .Should().Equal(_alphaQueue);
            services.Should().Contain(d => d.ServiceType == typeof(IBrokeredMessageReceiver<AlphaCommand>));
        }

        [Fact]
        public void MustRefuseASecondCallWhoseConnectionStringDiverges()
        {
            var services = new ServiceCollection();

            var registerBeta = RegisterBetaAfterAlpha(services, _divergentConnectionString);

            registerBeta.Should().Throw<NotSupportedException>()
                        .Which.Message.Should().Contain(nameof(SqlServiceBrokerOptions.ConnectionString))
                                       .And.Contain("https://github.com/brenpike/Chatter/issues/542")
                                       .And.NotContain(_connectionString)
                                       .And.NotContain(_divergentConnectionString);
        }

        [Fact]
        public void MustNameEveryDivergingSettingWhenASecondCallIsRefused()
        {
            var services = new ServiceCollection();
            var builder = NewBareBuilder(services);
            builder.AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(_connectionString));

            Action registerDivergent = () => builder.AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(_connectionString)
                                                                                .WithReceiverTimeout(5000)
                                                                                .UseConversationEncryption());

            registerDivergent.Should().Throw<NotSupportedException>()
                             .Which.Message.Should().Contain(nameof(SqlServiceBrokerOptions.ReceiverTimeoutInMilliseconds))
                                            .And.Contain(nameof(SqlServiceBrokerOptions.ConversationEncryption))
                                            .And.NotContain(nameof(SqlServiceBrokerOptions.ConnectionString));
        }

        [Fact]
        public void MustLeaveTheServiceCollectionExactlyAsItWasWhenADivergentCallIsRefused()
        {
            var services = new ServiceCollection();
            var registerBeta = RegisterBetaAfterAlpha(services, _divergentConnectionString);
            var beforeTheRefusedCall = services.ToList();

            registerBeta.Should().Throw<NotSupportedException>();

            services.Should().Equal(beforeTheRefusedCall,
                                    "a refused call must not register anything - the same descriptors must still sit in the same slots");
        }

        [Fact]
        public void MustLeaveTheDiscoveredReceiversUnchangedWhenADivergentCallIsRefused()
        {
            var services = new ServiceCollection();
            var registerBeta = RegisterBetaAfterAlpha(services, _divergentConnectionString);
            var discoveredBeforeTheRefusedCall = EffectiveDiscoveredReceiverRegistry(services).DiscoveredReceivers.ToList();

            registerBeta.Should().Throw<NotSupportedException>();

            EffectiveDiscoveredReceiverRegistry(services).DiscoveredReceivers.Should().Equal(discoveredBeforeTheRefusedCall);
        }

        public static TheoryData<string> BuildRefusals() => new TheoryData<string>
        {
            "no options",
            "blank connection string",
            "blank message body type",
        };

        private static void ConfigureBuildRefusal(SqlServiceBrokerOptionsBuilder builder, string refusal)
        {
            builder.AddQueueReceiver<AlphaCommand>(_alphaQueue);
            switch (refusal)
            {
                case "no options":
                    return;
                case "blank connection string":
                    builder.AddSqlServiceBrokerOptions(" ");
                    return;
                case "blank message body type":
                    builder.AddSqlServiceBrokerOptions(_connectionString, messageBodyType: " ");
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Unknown Build() refusal.");
            }
        }

        [Theory]
        [MemberData(nameof(BuildRefusals))]
        public void MustLeaveTheServiceCollectionExactlyAsItWasWhenBuildRefusesTheOptions(string refusal)
        {
            var services = new ServiceCollection();
            var builder = NewBareBuilder(services);
            var beforeTheRefusedCall = services.ToList();

            Action register = () => builder.AddSqlServiceBroker(o => ConfigureBuildRefusal(o, refusal));

            register.Should().Throw<ArgumentNullException>();
            services.Should().Equal(beforeTheRefusedCall,
                                    "a refused call must not register anything - the same descriptors must still sit in the same slots");
        }

        [Fact]
        public void MustRefuseASecondCallWithoutAnOptionsBuilder()
        {
            var services = new ServiceCollection();
            var builder = NewBareBuilder(services);
            builder.AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(_connectionString));

            Action registerWithoutOptions = () => builder.AddSqlServiceBroker(optionsBuilder: null);

            registerWithoutOptions.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustRefuseAQueueReceiverForABrokeredMessageDecoratedTypeWhenItIsAdded()
        {
            var optionsBuilder = new SqlServiceBrokerOptionsBuilder(new ServiceCollection());

            Action addDecoratedReceiver = () => optionsBuilder.AddQueueReceiver<DecoratedCommand>(_alphaQueue);

            addDecoratedReceiver.Should().Throw<InvalidOperationException>()
                                .WithMessage($"*{nameof(DecoratedCommand)}*{nameof(BrokeredMessageAttribute)}*");
        }
    }
}
