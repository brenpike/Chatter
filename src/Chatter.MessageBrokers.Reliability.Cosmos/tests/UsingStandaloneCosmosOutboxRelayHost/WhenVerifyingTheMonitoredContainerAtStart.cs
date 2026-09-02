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

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingStandaloneCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes the STANDALONE relay host's start-time reconciliation of the monitored container's DECLARED
    /// configuration against its GROUND TRUTH (<see cref="MonitoredContainerContract"/>): a mistyped partition-key path
    /// (#362) and a purging default time-to-live (#363) are rejected BEFORE the change-feed processor is built, so a
    /// misconfigured container fails the host's start rather than silently re-publishing or silently losing pending
    /// Outbox Documents at runtime. The cheap in-memory consumer-group backstop still runs FIRST, so a collision costs
    /// no metadata round-trip.
    /// </summary>
    public class WhenVerifyingTheMonitoredContainerAtStart
    {
        private const string DatabaseId = "shop";
        private const string MonitoredContainerId = "orders";
        private const string LeaseContainerId = "orders-leases";

        private static readonly IReadOnlyList<string> DeclaredPartitionKeyPath = Path("/tenantId");

        // Reaching GetChangeFeedProcessorBuilder is the observable proof that start-time verification PASSED:
        // ChangeFeedProcessorBuilder is sealed and unmockable, so the mocked builder call throws this sentinel instead
        // of returning a builder the host could go on to Build()/StartAsync().
        private static readonly InvalidOperationException ReachedTheProcessorBuilder =
            new InvalidOperationException("the host reached GetChangeFeedProcessorBuilder");

        private static IReadOnlyList<string> Path(params string[] segments) => Array.AsReadOnly(segments);

        // A container whose resolved physical identity (.Id + .Database.Id) and account endpoint
        // (.Database.Client.Endpoint) are fixed, so the ground-truth source-identity key resolves without a live SDK.
        private static Mock<Container> PhysicalContainer(string databaseId, string containerId, string endpoint = "https://acct.documents.azure.com/")
        {
            var client = new Mock<CosmosClient>();
            client.SetupGet(c => c.Endpoint).Returns(new Uri(endpoint));

            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns(databaseId);
            database.SetupGet(d => d.Client).Returns(client.Object);

            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns(containerId);
            container.SetupGet(c => c.Database).Returns(database.Object);
            return container;
        }

        // A monitored container reporting the supplied GROUND TRUTH through ReadContainerAsync. ContainerProperties
        // exposes PartitionKeyPaths only through its constructors and ContainerResponse ships a mocking constructor, so
        // both sides of the read are real SDK types.
        private static Mock<Container> MonitoredContainer(IReadOnlyList<string> actualPartitionKeyPaths, int? defaultTimeToLive)
        {
            var properties = new ContainerProperties(MonitoredContainerId, actualPartitionKeyPaths)
            {
                DefaultTimeToLive = defaultTimeToLive,
            };

            var response = new Mock<ContainerResponse>();
            response.SetupGet(r => r.Resource).Returns(properties);

            Mock<Container> container = PhysicalContainer(DatabaseId, MonitoredContainerId);
            container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(response.Object);
            container.Setup(c => c.GetChangeFeedProcessorBuilder(It.IsAny<string>(), It.IsAny<Container.ChangeFeedStreamHandler>()))
                     .Throws(ReachedTheProcessorBuilder);
            return container;
        }

        private static StandaloneCosmosOutboxRelayHostedService StandaloneHost(Container monitored,
                                                                              Container lease,
                                                                              StandaloneRelayProcessorRegistry processorRegistry = null)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => monitored,
                    LeaseContainerFactory = _ => lease,
                    PartitionKeyPath = DeclaredPartitionKeyPath,
                },
                processorRegistry);

        // #362: a declared path that does not match the container's actual path recovers a null-component PartitionKey,
        // so the delivered stamp 404s AFTER the publish succeeded and the document re-publishes on every pass. The host
        // refuses to start rather than begin that loop.
        [Fact]
        public async Task MustRejectAMonitoredContainerWhosePartitionKeyPathDoesNotMatch()
        {
            Mock<Container> monitored = MonitoredContainer(Path("/customerId"), defaultTimeToLive: -1);
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, PhysicalContainer(DatabaseId, LeaseContainerId).Object);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "a declared partition-key path that does not match the container's actual path is rejected at start"))
                .Which.Message.Should().Contain("/tenantId").And.Contain("/customerId");
            monitored.Verify(c => c.GetChangeFeedProcessorBuilder(It.IsAny<string>(), It.IsAny<Container.ChangeFeedStreamHandler>()),
                Times.Never,
                "the contract is verified BEFORE the change-feed processor is built, so a misconfigured container never gets a processor");
        }

        // #363: a POSITIVE default time-to-live deletes a still-pending Outbox Document (written with no ttl field)
        // before the relay ever drains it, turning at-least-once into zero-times.
        [Fact]
        public async Task MustRejectAMonitoredContainerWhoseDefaultTimeToLiveWouldPurgeAPendingDocument()
        {
            Mock<Container> monitored = MonitoredContainer(DeclaredPartitionKeyPath, defaultTimeToLive: 3600);
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, PhysicalContainer(DatabaseId, LeaseContainerId).Object);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "a positive default time-to-live would purge a pending outbox document before the relay drained it"))
                .Which.Message.Should().Contain("3600");
            monitored.Verify(c => c.GetChangeFeedProcessorBuilder(It.IsAny<string>(), It.IsAny<Container.ChangeFeedStreamHandler>()),
                Times.Never,
                "the contract is verified BEFORE the change-feed processor is built");
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(null)]
        public async Task MustBuildTheProcessorWhenTheContainerSatisfiesTheContract(int? defaultTimeToLive)
        {
            Mock<Container> monitored = MonitoredContainer(DeclaredPartitionKeyPath, defaultTimeToLive);
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, PhysicalContainer(DatabaseId, LeaseContainerId).Object);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().BeSameAs(ReachedTheProcessorBuilder,
                    "a matching partition-key path and a non-purging default time-to-live pass the contract, so the host goes on to build its change-feed processor");
            monitored.Verify(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "the container properties are read exactly once per start");
        }

        // Regression guard on the ORDERING: the cheap in-memory consumer-group backstop must fire before the network
        // round-trip, so a second ground-truth-defaulted host over the same physical identity fails without ever asking
        // Cosmos for the container's properties.
        [Fact]
        public async Task MustFailOnTheConsumerGroupCollisionBeforeReadingTheMonitoredContainer()
        {
            var registry = new StandaloneRelayProcessorRegistry();
            Container lease = PhysicalContainer(DatabaseId, LeaseContainerId).Object;

            StandaloneCosmosOutboxRelayHostedService first = StandaloneHost(
                MonitoredContainer(DeclaredPartitionKeyPath, defaultTimeToLive: -1).Object, lease, registry);
            first.RegisterStartTimeProcessorIdentity(first.ResolveProcessorDescriptor());

            Mock<Container> collidingMonitored = MonitoredContainer(DeclaredPartitionKeyPath, defaultTimeToLive: -1);
            StandaloneCosmosOutboxRelayHostedService second = StandaloneHost(collidingMonitored.Object, lease, registry);

            Func<Task> start = () => second.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "two ground-truth-defaulted hosts over the same physical identity form one consumer group, which is rejected at host start"))
                .Which.Message.Should().Contain(second.ResolveProcessorDescriptor().ProcessorName);
            collidingMonitored.Verify(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "the in-memory collision backstop runs FIRST, so a collision costs no container-metadata round-trip");
        }
    }
}
