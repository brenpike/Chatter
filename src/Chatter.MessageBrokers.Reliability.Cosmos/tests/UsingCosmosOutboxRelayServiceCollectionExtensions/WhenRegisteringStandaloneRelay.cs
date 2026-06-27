using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
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
    }
}
