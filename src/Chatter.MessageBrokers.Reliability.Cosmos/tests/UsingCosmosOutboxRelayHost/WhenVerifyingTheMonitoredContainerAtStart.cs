using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes the document-tier relay host's start-time reconciliation of every monitored container's DECLARED
    /// configuration against its GROUND TRUTH (#362 partition-key path, #363 default time-to-live). The reconciliation is
    /// a TWO-PASS start: EVERY descriptor is verified BEFORE any change-feed processor is built. Per-descriptor
    /// verify-then-start would leave earlier processors RUNNING when a later descriptor fails — and StopAsync is never
    /// invoked for a hosted service whose StartAsync threw, so those processors would leak.
    /// </summary>
    public class WhenVerifyingTheMonitoredContainerAtStart
    {
        private const string DatabaseId = "shop";
        private static readonly IReadOnlyList<string> DeclaredPartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private sealed class CreateOrder : ICommand { }
        private sealed class ShipOrder : ICommand { }

        // A monitored container whose ReadContainerAsync reports the supplied ground-truth partition-key path and default
        // time-to-live, mirroring the UsingMonitoredContainerContract harness: ContainerProperties exposes
        // PartitionKeyPaths only through its constructors and ContainerResponse ships a mocking constructor, so both sides
        // of the read are real SDK types.
        private static Mock<Container> MonitoredContainer(string containerId,
                                                          IReadOnlyList<string> actualPartitionKeyPaths,
                                                          int? defaultTimeToLive)
        {
            var properties = new ContainerProperties(containerId, actualPartitionKeyPaths)
            {
                DefaultTimeToLive = defaultTimeToLive,
            };

            var response = new Mock<ContainerResponse>();
            response.SetupGet(r => r.Resource).Returns(properties);

            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns(DatabaseId);

            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns(containerId);
            container.SetupGet(c => c.Database).Returns(database.Object);
            container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Returns(() => Task.FromResult(response.Object));
            return container;
        }

        // The declared-source-identity (advanced) registration path keeps the host off the resolved handle's account
        // endpoint, so the lease handle needs no identity setup at all.
        private static DocumentReliabilityRegistration RegistrationFor<TCommand>(string declaredSourceIdentity,
                                                                                 Container monitoredContainer)
            where TCommand : ICommand
        {
            Container leaseContainer = Mock.Of<Container>();
            return new DocumentReliabilityRegistration(
                typeof(TCommand),
                DatabaseId,
                declaredSourceIdentity + ":document",
                declaredSourceIdentity + ":lease",
                _ => new PartitionKey("pk"),
                DeclaredPartitionKeyPath,
                documentContainerFactory: _ => monitoredContainer,
                leaseContainerFactory: _ => leaseContainer,
                declaredSourceIdentity: new CosmosSourceIdentity(declaredSourceIdentity, declaredSourceIdentity + "-lease"));
        }

        private static CosmosOutboxRelayHostedService Host(params DocumentReliabilityRegistration[] registrations)
        {
            var registry = new DocumentReliabilityRegistry();
            foreach (DocumentReliabilityRegistration registration in registrations)
            {
                // Add is internal; InternalsVisibleTo exposes it to the test assembly.
                registry.Add(registration);
            }

            var services = new ServiceCollection();
            services.AddSingleton(new Mock<CosmosClient>(MockBehavior.Strict).Object);

            return new CosmosOutboxRelayHostedService(
                registry,
                new CosmosContainerFactory(services.BuildServiceProvider()),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>());
        }

        private static void ShouldHaveStartedNoProcessor(Mock<Container> monitoredContainer, string because)
            => monitoredContainer.Verify(c => c.GetChangeFeedProcessorBuilder(It.IsAny<string>(), It.IsAny<Container.ChangeFeedStreamHandler>()), Times.Never, because);

        [Fact]
        public async Task MustFailStartOnAPartitionKeyPathMismatchBeforeBuildingAnyProcessor()
        {
            Mock<Container> monitored = MonitoredContainer("orders", Array.AsReadOnly(new[] { "/orgId" }), defaultTimeToLive: -1);
            CosmosOutboxRelayHostedService host = Host(RegistrationFor<CreateOrder>("orders-source", monitored.Object));

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "a declared partition-key path that does not match the container recovers a null-component partition key, so the delivered stamp 404s and the same message re-publishes forever (#362)"))
                .Which.Message.Should().Contain("/tenantId");
            ShouldHaveStartedNoProcessor(monitored, "a container that failed verification must never have a change-feed processor built for it");
        }

        [Fact]
        public async Task MustFailStartOnAPositiveDefaultTimeToLiveBeforeBuildingAnyProcessor()
        {
            Mock<Container> monitored = MonitoredContainer("orders", DeclaredPartitionKeyPath, defaultTimeToLive: 3600);
            CosmosOutboxRelayHostedService host = Host(RegistrationFor<CreateOrder>("orders-source", monitored.Object));

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "a positive container default time-to-live deletes a still-pending outbox document before the relay ever drains it (#363)"))
                .Which.Message.Should().Contain("3600");
            ShouldHaveStartedNoProcessor(monitored, "a container that failed verification must never have a change-feed processor built for it");
        }

        // The two-pass PIN: with per-descriptor verify-then-start, the VALID descriptor's processor would already be
        // running when the invalid one throws — and StopAsync is not invoked for a hosted service whose StartAsync threw,
        // so that processor would leak for the lifetime of the process.
        [Fact]
        public async Task MustBuildNoProcessorForAValidDescriptorWhenALaterDescriptorFailsVerification()
        {
            Mock<Container> valid = MonitoredContainer("orders", DeclaredPartitionKeyPath, defaultTimeToLive: -1);
            Mock<Container> invalid = MonitoredContainer("shipments", Array.AsReadOnly(new[] { "/orgId" }), defaultTimeToLive: -1);

            CosmosOutboxRelayHostedService host = Host(
                RegistrationFor<CreateOrder>("orders-source", valid.Object),
                RegistrationFor<ShipOrder>("shipments-source", invalid.Object));

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            await start.Should().ThrowAsync<InvalidOperationException>(
                "one misconfigured monitored container must take the whole host down at start");
            ShouldHaveStartedNoProcessor(valid, "every descriptor is verified before any processor is built, so a later failure cannot leave an earlier processor running with no StopAsync to stop it");
        }

        [Fact]
        public async Task MustVerifyNothingAndStartNothingForAnEmptyRegistry()
        {
            CosmosOutboxRelayHostedService host = Host();

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            await start.Should().NotThrowAsync("a host with no document-tier registrations has no monitored container to verify and no processor to start");
        }
    }
}
