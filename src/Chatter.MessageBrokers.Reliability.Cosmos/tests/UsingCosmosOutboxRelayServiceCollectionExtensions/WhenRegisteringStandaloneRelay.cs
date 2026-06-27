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
        public void MustSurfaceTheConfiguredBodyResolverToTheConstructedRelay()
        {
            var resolver = Mock.Of<IOutboxBodyResolver>();
            ServiceProvider provider = BuildProviderWith(ValidConfigure(options => options.BodyResolverFactory = _ => resolver));

            StandaloneCosmosOutboxRelayHostedService host = provider.GetServices<IHostedService>()
                .OfType<StandaloneCosmosOutboxRelayHostedService>()
                .Single();

            host.BodyResolver.Should().BeSameAs(resolver,
                "the configured body-resolver factory is resolved once at host construction and supplied to the relay");
        }
    }
}
