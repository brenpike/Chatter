using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelayServiceCollectionExtensions
{
    /// <summary>
    /// Characterizes the standalone relay DI surface: <see cref="CosmosOutboxRelayServiceCollectionExtensions.AddCosmosOutboxRelay"/>
    /// registers a SEPARATE <see cref="IHostedService"/> (not the registry-driven relay host), is REPEATABLE (each call
    /// adds another hosted service), validates required options, and surfaces a configured body resolver to the constructed
    /// relay. It must NOT touch the <see cref="DocumentReliabilityRegistry"/>.
    /// </summary>
    public class WhenRegisteringStandaloneRelay
    {
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private static Container AnyContainer()
        {
            var client = new Mock<CosmosClient>();
            client.SetupGet(c => c.Endpoint).Returns(new Uri("https://acct.documents.azure.com/"));
            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns("shop");
            database.SetupGet(d => d.Client).Returns(client.Object);
            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns("orders");
            container.SetupGet(c => c.Database).Returns(database.Object);
            return container.Object;
        }

        private static Action<CosmosOutboxRelayOptions> ValidConfigure(Action<CosmosOutboxRelayOptions> extra = null)
        {
            Container monitored = AnyContainer();
            Container lease = AnyContainer();
            return options =>
            {
                options.MonitoredContainerFactory = _ => monitored;
                options.LeaseContainerFactory = _ => lease;
                options.PartitionKeyPath = PartitionKeyPath;
                extra?.Invoke(options);
            };
        }

        private static Action<CosmosOutboxRelayOptions> ValidConfigureWithDeclaredIdentity(string monitoredSourceIdentity,
                                                                                          string leaseSourceIdentity,
                                                                                          Action<CosmosOutboxRelayOptions> extra = null)
            => ValidConfigure(options =>
            {
                options.MonitoredSourceIdentity = monitoredSourceIdentity;
                options.LeaseSourceIdentity = leaseSourceIdentity;
                extra?.Invoke(options);
            });

        private static ServiceProvider BuildProviderWith(params Action<CosmosOutboxRelayOptions>[] registrations)
        {
            var services = new ServiceCollection();
            services.AddSingleton(Mock.Of<IMessagingInfrastructureProvider>());
            services.AddSingleton(Mock.Of<IBodyConverterFactory>());
            foreach (Action<CosmosOutboxRelayOptions> registration in registrations)
            {
                services.AddCosmosOutboxRelay(registration);
            }
            return services.BuildServiceProvider();
        }

        private static ServiceCollection ServicesWithBrokerDependencies()
        {
            var services = new ServiceCollection();
            services.AddSingleton(Mock.Of<IMessagingInfrastructureProvider>());
            services.AddSingleton(Mock.Of<IBodyConverterFactory>());
            return services;
        }

        // Distinct concrete resolver types so the typed/keyed overloads' registration + factory wiring is observable.
        private sealed class StubBodyResolver : IOutboxBodyResolver
        {
            public Task<OutboundBrokeredMessage> ResolveAsync(OutboxDrainContext context, CancellationToken cancellationToken = default)
                => Task.FromResult<OutboundBrokeredMessage>(null);
        }

        private sealed class OtherStubBodyResolver : IOutboxBodyResolver
        {
            public Task<OutboundBrokeredMessage> ResolveAsync(OutboxDrainContext context, CancellationToken cancellationToken = default)
                => Task.FromResult<OutboundBrokeredMessage>(null);
        }

        [Fact]
        public void MustRegisterASeparateHostedService()
        {
            var services = new ServiceCollection();

            services.AddCosmosOutboxRelay(ValidConfigure());

            ServiceDescriptor descriptor = services.Single(d => d.ServiceType == typeof(IHostedService));
            descriptor.ImplementationFactory.Should().NotBeNull("the standalone relay is registered as a factory-built hosted service");
            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton, "a hosted service is a singleton");
        }

        [Fact]
        public void MustResolveToTheStandaloneHostType()
        {
            ServiceProvider provider = BuildProviderWith(ValidConfigure());

            provider.GetServices<IHostedService>()
                .OfType<StandaloneCosmosOutboxRelayHostedService>()
                .Should().HaveCount(1, "AddCosmosOutboxRelay registers the standalone relay host");
        }

        /// <summary>
        /// The always-on observability channel of #361. The standalone host is built by an explicit FACTORY, so nothing
        /// injects its logger for it: without the factory resolving one, the give-up Error log and the change-feed fault
        /// log are silent in every production application while the meters still record — which is exactly the silence
        /// #361 exists to close.
        /// </summary>
        // The wired logger is private to the host and both sinks that take it fire only on a live change-feed fault, so
        // the category the application's ILoggerFactory was asked for is the observable evidence available here.
        [Fact]
        public void MustResolveTheApplicationsLoggerForTheStandaloneHost()
        {
            var loggerProvider = new CategoryRecordingLoggerProvider();
            ServiceCollection services = ServicesWithBrokerDependencies();
            services.AddLogging(logging => logging.AddProvider(loggerProvider));
            services.AddCosmosOutboxRelay(ValidConfigure());

            using ServiceProvider provider = services.BuildServiceProvider();
            provider.GetServices<IHostedService>().OfType<StandaloneCosmosOutboxRelayHostedService>().Should().ContainSingle();

            loggerProvider.Categories.Should().Contain(typeof(StandaloneCosmosOutboxRelayHostedService).FullName,
                "the registration must hand the host a logger from the application's logging, or #361's always-on log channel is dead in production");
        }

        /// <summary>
        /// Logging stays OPTIONAL: an application that configured none must still get a working relay, so the logger is
        /// resolved with <c>GetService</c> (null when absent) rather than demanded.
        /// </summary>
        [Fact]
        public void MustConstructTheStandaloneHostWhenNoLoggingIsConfigured()
        {
            using ServiceProvider provider = BuildProviderWith(ValidConfigure());

            Action act = () => provider.GetServices<IHostedService>().ToList();

            act.Should().NotThrow("observability is never a construction prerequisite for the relay");
        }

        // An ILoggerProvider recording the categories the application's ILoggerFactory was asked to create a logger for.
        private sealed class CategoryRecordingLoggerProvider : ILoggerProvider
        {
            public List<string> Categories { get; } = new List<string>();

            public ILogger CreateLogger(string categoryName)
            {
                Categories.Add(categoryName);
                return NullLogger.Instance;
            }

            public void Dispose()
            {
            }
        }

        [Fact]
        public void MustNotRegisterTheRegistryDrivenRelayHostOrTheRegistry()
        {
            var services = new ServiceCollection();

            services.AddCosmosOutboxRelay(ValidConfigure());

            services.Should().NotContain(d => d.ServiceType == typeof(DocumentReliabilityRegistry),
                "the standalone relay must not touch the document-reliability registry");
            services.Should().NotContain(d => d.ImplementationType == typeof(CosmosOutboxRelayHostedService),
                "the standalone relay is separate from the registry-driven relay host");
        }

        [Fact]
        public void MustBeRepeatableAcrossMultipleCalls()
        {
            var services = new ServiceCollection();

            services.AddCosmosOutboxRelay(ValidConfigure());
            services.AddCosmosOutboxRelay(ValidConfigure());

            services.Count(d => d.ServiceType == typeof(IHostedService))
                .Should().Be(2, "each AddCosmosOutboxRelay call registers another standalone relay hosted service");
        }

        [Fact]
        public void MustResolveTwoStandaloneHostsForTwoCalls()
        {
            ServiceProvider provider = BuildProviderWith(ValidConfigure(), ValidConfigure());

            provider.GetServices<IHostedService>()
                .OfType<StandaloneCosmosOutboxRelayHostedService>()
                .Should().HaveCount(2, "two registrations resolve to two standalone relay hosts");
        }

        // PR2-STEP-001 declared-collision guard: two standalone relays declaring the SAME (MonitoredSourceIdentity,
        // LeaseSourceIdentity) pair derive the same processor name + lease — one consumer group — so the second registration
        // fails fast at AddCosmosOutboxRelay with InvalidOperationException rather than silently wedging at runtime.
        [Fact]
        public void MustThrowAtRegistrationWhenASecondRelayDeclaresTheSameSourceIdentity()
        {
            var services = new ServiceCollection();

            services.AddCosmosOutboxRelay(ValidConfigureWithDeclaredIdentity("orders-source", "orders-lease-source"));
            Action act = () => services.AddCosmosOutboxRelay(ValidConfigureWithDeclaredIdentity("orders-source", "orders-lease-source"));

            act.Should().Throw<InvalidOperationException>(
                "a second standalone relay declaring the same source identity derives the same processor name + lease — one consumer group that wedges a filtered-out document — so it is rejected at registration")
                .WithMessage("*DISTINCT*", "the message instructs the caller to supply distinct source identities");
        }

        [Fact]
        public void MustRegisterBothWhenTwoRelaysDeclareDistinctSourceIdentities()
        {
            var services = new ServiceCollection();

            services.AddCosmosOutboxRelay(ValidConfigureWithDeclaredIdentity("orders-source", "orders-lease-source"));
            services.AddCosmosOutboxRelay(ValidConfigureWithDeclaredIdentity("ledger-source", "ledger-lease-source"));

            services.Count(d => d.ServiceType == typeof(IHostedService))
                .Should().Be(2, "distinct declared source identities derive distinct processor names, so both relays register");
        }

        [Fact]
        public void TypedOverloadMustThrowAtRegistrationWhenASecondRelayDeclaresTheSameSourceIdentity()
        {
            ServiceCollection services = ServicesWithBrokerDependencies();

            services.AddCosmosOutboxRelay<StubBodyResolver>(ValidConfigureWithDeclaredIdentity("orders-source", "orders-lease-source"));
            Action act = () => services.AddCosmosOutboxRelay<StubBodyResolver>(ValidConfigureWithDeclaredIdentity("orders-source", "orders-lease-source"));

            act.Should().Throw<InvalidOperationException>(
                "the declared-collision guard rides the base AddCosmosOutboxRelay overload the typed overload delegates to");
        }

        [Fact]
        public void MustThrowWhenMonitoredContainerFactoryIsMissing()
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(options =>
            {
                options.LeaseContainerFactory = _ => AnyContainer();
                options.PartitionKeyPath = PartitionKeyPath;
            });

            act.Should().Throw<ArgumentException>("a monitored-container factory is required");
        }

        [Fact]
        public void MustThrowWhenLeaseContainerFactoryIsMissing()
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(options =>
            {
                options.MonitoredContainerFactory = _ => AnyContainer();
                options.PartitionKeyPath = PartitionKeyPath;
            });

            act.Should().Throw<ArgumentException>("a lease-container factory is required");
        }

        [Fact]
        public void MustThrowWhenPartitionKeyPathIsMissingOrEmpty()
        {
            var services = new ServiceCollection();

            Action missing = () => services.AddCosmosOutboxRelay(options =>
            {
                options.MonitoredContainerFactory = _ => AnyContainer();
                options.LeaseContainerFactory = _ => AnyContainer();
            });
            Action empty = () => services.AddCosmosOutboxRelay(options =>
            {
                options.MonitoredContainerFactory = _ => AnyContainer();
                options.LeaseContainerFactory = _ => AnyContainer();
                options.PartitionKeyPath = Array.Empty<string>();
            });

            missing.Should().Throw<ArgumentException>("a partition-key path is required");
            empty.Should().Throw<ArgumentException>("a non-empty partition-key path is required");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void MustRejectInvalidPartitionKeyPathSegment(string invalidSegment)
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(ValidConfigure(options =>
                options.PartitionKeyPath = new[] { "/tenantId", invalidSegment }));

            act.Should().Throw<ArgumentException>(
                "every partition-key path segment must be non-null and non-whitespace, mirroring the command-pipeline registration");
        }

        [Fact]
        public void MustNotBeAffectedByPostRegistrationMutationOfPartitionKeyPath()
        {
            CosmosOutboxRelayOptions captured = null;
            var mutablePath = new List<string> { "/tenantId" };

            var services = new ServiceCollection();
            services.AddCosmosOutboxRelay(ValidConfigure(options =>
            {
                options.PartitionKeyPath = mutablePath;
                captured = options;
            }));

            // Mutating the caller-owned list after registration must not corrupt the validated, captured path: the
            // standalone path snapshots the partition-key path through the same hardened validator the command pipeline uses.
            mutablePath[0] = "/corrupted";

            captured.PartitionKeyPath.Should().ContainSingle().Which.Should().Be("/tenantId",
                "AddCosmosOutboxRelay stores an independent snapshot of the partition-key path back onto the options");
        }

        [Fact]
        public void MustNotEagerlyInvokeTheBodyResolverFactoryAtHostConstruction()
        {
            var invocationCount = 0;
            ServiceProvider provider = BuildProviderWith(ValidConfigure(options => options.BodyResolverFactory = _ =>
            {
                invocationCount++;
                return Mock.Of<IOutboxBodyResolver>();
            }));

            // Resolving the hosted service constructs the host. The body-resolver factory must NOT be consulted at
            // construction: it is resolved per drained document from a fresh DI scope, so a scoped resolver never outlives
            // the document it drains and a per-request misconfiguration does not fault host construction.
            provider.GetServices<IHostedService>()
                .OfType<StandaloneCosmosOutboxRelayHostedService>()
                .Single();

            invocationCount.Should().Be(0,
                "the body-resolver factory is consulted per drained document from a scope, not once at host construction");
        }

        [Fact]
        public void MustThrowAtRegistrationWhenDeliveredStatusValueEqualsPending()
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(ValidConfigure(options => options.DeliveredStatusValue = "pending"));

            act.Should().Throw<ArgumentException>(
                "a delivered status equal to pending would never advance the document out of pending — the F2 invariant is enforced at registration, before the provider is built");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void MustThrowAtRegistrationWhenDeliveredTtlSecondsIsNonPositive(int deliveredTtlSeconds)
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(ValidConfigure(options => options.DeliveredTtlSeconds = deliveredTtlSeconds));

            act.Should().Throw<ArgumentException>(
                "a non-positive delivered TTL (including -1 retain-indefinitely) is rejected at registration");
        }

        [Theory]
        [InlineData("/state")]  // valid pointer but not anchored to the status field
        [InlineData("")]        // empty
        [InlineData("/")]       // no non-empty segment
        public void MustThrowAtRegistrationWhenStatusPatchPathIsUnanchoredOrInvalid(string statusPatchPath)
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(ValidConfigure(options => options.StatusPatchPath = statusPatchPath));

            act.Should().Throw<ArgumentException>(
                "a status patch path not anchored to the status field (or not a valid JSON pointer) is rejected at registration");
        }

        [Fact]
        public void MustNotExposeAConfigurableTtlPatchPath()
        {
            // The delivered stamp always targets the Cosmos-reserved "/ttl" path — the only path Cosmos self-purges on —
            // so a non-purging delivered stamp is unrepresentable. The ttl path is hard-wired, not a configurable knob.
            typeof(CosmosOutboxRelayOptions).GetProperty("TtlPatchPath").Should().BeNull(
                "the ttl patch path is not configurable; the delivered stamp is hard-wired to the reserved /ttl path");
        }

        [Fact]
        public void MustThrowAtRegistrationWhenPoisonAfterConsecutiveFailuresIsNegative()
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(ValidConfigure(options => options.PoisonAfterConsecutiveFailures = -1));

            act.Should().Throw<ArgumentException>(
                "a negative consecutive-failure threshold is meaningless — 0 is the off switch — and is rejected at registration, before the provider is built");
        }

        [Fact]
        public void MustThrowAtRegistrationWhenPoisonStatusValueEqualsPending()
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(ValidConfigure(options =>
            {
                options.PoisonAfterConsecutiveFailures = 3;
                options.PoisonStatusValue = CosmosOutboxDocument.StatusPending;
            }));

            act.Should().Throw<ArgumentException>(
                "a poison status equal to pending would leave the given-up document admitted forever — the very stall the policy exists to end");
        }

        [Fact]
        public void MustThrowAtRegistrationWhenPoisonStatusValueEqualsTheDeliveredStatusValue()
        {
            var services = new ServiceCollection();

            Action act = () => services.AddCosmosOutboxRelay(ValidConfigure(options =>
            {
                options.PoisonAfterConsecutiveFailures = 3;
                options.PoisonStatusValue = options.DeliveredStatusValue;
            }));

            act.Should().Throw<ArgumentException>(
                "a give-up stamped with the delivered value would be indistinguishable from an actual delivery");
        }

        [Fact]
        public void MustLeaveThePoisonPolicyOffByDefault()
        {
            var options = new CosmosOutboxRelayOptions();

            options.PoisonAfterConsecutiveFailures.Should().Be(0,
                "the poison policy is opt-in; an unconfigured relay keeps today's fail-closed behavior");
        }

        [Fact]
        public void TypedOverloadMustRegisterTheResolverAsScoped()
        {
            ServiceCollection services = ServicesWithBrokerDependencies();

            services.AddCosmosOutboxRelay<StubBodyResolver>(ValidConfigure());

            ServiceDescriptor descriptor = services.Single(d => d.ServiceType == typeof(StubBodyResolver));
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped,
                "the typed overload registers the resolver scoped so a fresh instance is resolved per drained document");
            services.Count(d => d.ServiceType == typeof(IHostedService)).Should().Be(1,
                "the typed overload delegates to the base AddCosmosOutboxRelay for hosted-service registration");
        }

        [Fact]
        public void TypedOverloadMustWireFactoryToResolveTheRegisteredResolverPerDocument()
        {
            CosmosOutboxRelayOptions captured = null;
            ServiceCollection services = ServicesWithBrokerDependencies();

            services.AddCosmosOutboxRelay<StubBodyResolver>(ValidConfigure(options => captured = options));

            captured.BodyResolverFactory.Should().NotBeNull("the typed overload owns wiring BodyResolverFactory to the registered resolver");
            ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope firstDocumentScope = provider.CreateScope();
            using IServiceScope secondDocumentScope = provider.CreateScope();

            IOutboxBodyResolver firstA = captured.BodyResolverFactory(firstDocumentScope.ServiceProvider);
            IOutboxBodyResolver firstB = captured.BodyResolverFactory(firstDocumentScope.ServiceProvider);
            IOutboxBodyResolver second = captured.BodyResolverFactory(secondDocumentScope.ServiceProvider);

            firstA.Should().BeOfType<StubBodyResolver>("the wired factory resolves the registered TResolver");
            firstA.Should().BeSameAs(firstB, "a scoped resolver is a single instance within one per-document scope");
            firstA.Should().NotBeSameAs(second, "a fresh per-document scope yields a fresh resolver");
        }

        [Fact]
        public void TypedOverloadMustUseTryAddSemanticsAndNotReplaceAnExistingResolverRegistration()
        {
            ServiceCollection services = ServicesWithBrokerDependencies();
            services.AddSingleton<StubBodyResolver>();

            services.AddCosmosOutboxRelay<StubBodyResolver>(ValidConfigure());

            ServiceDescriptor descriptor = services.Where(d => d.ServiceType == typeof(StubBodyResolver)).Should().ContainSingle(
                "TryAdd semantics must not double-register or replace an existing resolver registration").Subject;
            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton,
                "the pre-existing singleton registration is preserved (the typed overload's scoped registration is skipped)");
        }

        [Fact]
        public void TypedOverloadMustThrowWhenConfigureAlsoSetsBodyResolverFactory()
        {
            ServiceCollection services = ServicesWithBrokerDependencies();

            Action act = () => services.AddCosmosOutboxRelay<StubBodyResolver>(
                ValidConfigure(options => options.BodyResolverFactory = _ => Mock.Of<IOutboxBodyResolver>()));

            act.Should().Throw<ArgumentException>(
                "the typed overload owns the factory wiring; a caller also setting BodyResolverFactory is a conflict, not a silent override");
        }

        [Fact]
        public void KeyedOverloadMustRegisterAKeyedScopedResolverAndWireTheFactory()
        {
            const string key = "orders-relay";
            CosmosOutboxRelayOptions captured = null;
            ServiceCollection services = ServicesWithBrokerDependencies();

            services.AddCosmosOutboxRelay<StubBodyResolver>(key, ValidConfigure(options => captured = options));

            ServiceDescriptor descriptor = services.Single(d => d.ServiceType == typeof(IOutboxBodyResolver) && d.IsKeyedService);
            descriptor.ServiceKey.Should().Be(key);
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped, "the keyed overload registers a keyed-scoped resolver");
            descriptor.KeyedImplementationType.Should().Be(typeof(StubBodyResolver));

            captured.BodyResolverFactory.Should().NotBeNull();
            ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope documentScope = provider.CreateScope();
            captured.BodyResolverFactory(documentScope.ServiceProvider).Should().BeOfType<StubBodyResolver>(
                "the wired factory resolves the keyed resolver for the relay's key");
        }

        [Fact]
        public void KeyedOverloadMustResolveDistinctResolversForDistinctKeys()
        {
            const string ordersKey = "orders-relay";
            const string shipmentsKey = "shipments-relay";
            CosmosOutboxRelayOptions capturedOrders = null;
            CosmosOutboxRelayOptions capturedShipments = null;
            ServiceCollection services = ServicesWithBrokerDependencies();

            services.AddCosmosOutboxRelay<StubBodyResolver>(ordersKey, ValidConfigure(options => capturedOrders = options));
            services.AddCosmosOutboxRelay<OtherStubBodyResolver>(shipmentsKey, ValidConfigure(options => capturedShipments = options));

            ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope documentScope = provider.CreateScope();
            IOutboxBodyResolver orders = capturedOrders.BodyResolverFactory(documentScope.ServiceProvider);
            IOutboxBodyResolver shipments = capturedShipments.BodyResolverFactory(documentScope.ServiceProvider);

            orders.Should().BeOfType<StubBodyResolver>("each key binds its own resolver type");
            shipments.Should().BeOfType<OtherStubBodyResolver>("each key binds its own resolver type");
            orders.Should().NotBeSameAs(shipments, "two distinct keys resolve two distinct resolvers");
            services.Count(d => d.ServiceType == typeof(IHostedService)).Should().Be(2,
                "two keyed relays register two hosted services");
        }

        [Fact]
        public void KeyedOverloadMustThrowWhenConfigureAlsoSetsBodyResolverFactory()
        {
            ServiceCollection services = ServicesWithBrokerDependencies();

            Action act = () => services.AddCosmosOutboxRelay<StubBodyResolver>(
                "orders-relay",
                ValidConfigure(options => options.BodyResolverFactory = _ => Mock.Of<IOutboxBodyResolver>()));

            act.Should().Throw<ArgumentException>(
                "the keyed overload owns the factory wiring; a caller also setting BodyResolverFactory is a conflict, not a silent override");
        }
    }
}
