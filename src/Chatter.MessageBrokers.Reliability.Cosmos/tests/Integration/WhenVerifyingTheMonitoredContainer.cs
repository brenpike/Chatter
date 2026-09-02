using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // The relay's start-time reconciliation of a monitored container's DECLARED configuration against its GROUND TRUTH
    // (#362, #363), proven against REAL emulator containers rather than a mocked ReadContainerAsync. The unit suite
    // (UsingMonitoredContainerContract / UsingStandaloneCosmosOutboxRelayHost / UsingCosmosOutboxRelayHost) already owns
    // every assertion that must protect CI; what only a real container can add is that the SDK's ContainerProperties
    // actually reports back what the contract assumes it reports — the ttl an emulator-created container carries and the
    // full ordered PartitionKeyPaths of a HIERARCHICAL container.
    //
    // Each test provisions its OWN monitored container so a started change-feed processor never drains another test's
    // documents out of the collection's shared containers, and declares a UNIQUE source-identity pair so its processor
    // name never joins another relay's consumer group.
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenVerifyingTheMonitoredContainer
    {
        // A container at the suite's /pk path holding one pending outbox document, monitored under a MISMATCHED
        // declared path (#362).
        private const string MismatchedPathContainerName = "contract-mismatched-path";
        // A container whose POSITIVE default time-to-live would purge a still-pending outbox document (#363).
        private const string PurgingContainerName = "contract-purging-ttl";
        // The contract-satisfying container of the negative control: correct path, non-purging ttl.
        private const string ContractSatisfiedContainerName = "contract-satisfied";
        // A container with a two-segment HIERARCHICAL partition key.
        private const string HierarchicalContainerName = "contract-hierarchical";

        // The rejected value of the #363 probe. Deliberately small and free of digits shared with any other token in
        // the violation message, so asserting the message names it cannot pass by accident.
        private const int PurgingDefaultTimeToLive = 5;
        private const int NonPurgingDefaultTimeToLive = -1;

        private static readonly IReadOnlyList<string> DeclaredPartitionKeyPath = Path(CosmosTestClient.PartitionKeyPath);
        private static readonly IReadOnlyList<string> MismatchedPartitionKeyPath = Path("/tenantId");
        private static readonly IReadOnlyList<string> HierarchicalPartitionKeyPaths = Path("/tenantId", "/orderId");

        private readonly CosmosEmulatorFixture _emulator;

        public WhenVerifyingTheMonitoredContainer(CosmosEmulatorFixture emulator) => _emulator = emulator;

        // #362 over a REAL container: a declared path that does not match the container's actual path recovers a
        // null-component PartitionKey, so the delivered stamp 404s AFTER the publish already succeeded and the same
        // document re-publishes on every change-feed pass. The host must refuse to start rather than begin that loop —
        // asserted here against a container that actually HOLDS a pending outbox document, so a host that started would
        // have had one to publish.
        [RequiresDockerFact]
        public async Task RejectsAMismatchedPartitionKeyPathAndLeavesThePendingDocumentUndrained()
        {
            await using CosmosTestClient testClient = await CreateTestClientAsync();
            Container monitored = await testClient.CreateContainerWithDefaultTimeToLiveAsync(MismatchedPathContainerName, NonPurgingDefaultTimeToLive);

            string partition = UniquePartition();
            await SeedPendingOutboxDocumentAsync(monitored, partition);

            StandaloneCosmosOutboxRelayHostedService host = RelayHost(monitored, LeaseContainer(testClient), MismatchedPartitionKeyPath);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "a declared partition-key path that does not match the real container's path makes every delivered stamp 404 after a successful publish"))
                .Which.Message.Should().Contain(MismatchedPartitionKeyPath[0]).And.Contain(CosmosTestClient.PartitionKeyPath);

            CosmosEdge.OutboxWireShape? outbox = await CosmosEdge.ReadOutboxWireShapeAsync(monitored, partition);
            outbox.Should().NotBeNull("the seeded outbox document is still in the container");
            outbox.Value.Status.Should().Be(CosmosOutboxDocument.StatusPending,
                "the host never started, so the document was neither published nor stamped — the publish-then-404 loop never began");
        }

        // #363 over a REAL container: the emulator-created container reports the positive default time-to-live the
        // contract rejects, and the violation names the offending value so an operator can see WHICH ttl to correct.
        [RequiresDockerFact]
        public async Task RejectsAContainerWhoseDefaultTimeToLiveWouldPurgeAPendingDocument()
        {
            await using CosmosTestClient testClient = await CreateTestClientAsync();
            Container monitored = await testClient.CreateContainerWithDefaultTimeToLiveAsync(PurgingContainerName, PurgingDefaultTimeToLive);

            StandaloneCosmosOutboxRelayHostedService host = RelayHost(monitored, LeaseContainer(testClient), DeclaredPartitionKeyPath);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "a positive default time-to-live deletes a pending outbox document — which carries no ttl field — before the relay ever drains it"))
                .Which.Message.Should().Contain(PurgingDefaultTimeToLive.ToString()).And.Contain(PurgingContainerName);
        }

        // NEGATIVE CONTROL. Without it an always-throwing guard would still let both rejection tests above pass: a real
        // container that satisfies the contract must let the host start and build its change-feed processor.
        [RequiresDockerFact]
        public async Task StartsOverAContainerThatSatisfiesTheContract()
        {
            await using CosmosTestClient testClient = await CreateTestClientAsync();
            Container monitored = await testClient.CreateContainerWithDefaultTimeToLiveAsync(ContractSatisfiedContainerName, NonPurgingDefaultTimeToLive);

            StandaloneCosmosOutboxRelayHostedService host = RelayHost(monitored, LeaseContainer(testClient), DeclaredPartitionKeyPath);

            await StartAndStopAsync(host,
                "a matching partition-key path and a non-purging default time-to-live pass the contract, so a correctly-configured relay still starts");
        }

        // The one assertion no mock can give: the contract compares the declared path against whatever the SDK reports
        // as a HIERARCHICAL container's PartitionKeyPaths. Declaring only the first segment is rejected with the SECOND
        // segment named — a string only the SDK's ground truth could have supplied — and the full ordered declaration
        // passes, so the validator's assumption about the SDK's shape is proven, not assumed.
        [RequiresDockerFact]
        public async Task ValidatesAgainstTheHierarchicalContainersActualPartitionKeyPaths()
        {
            await using CosmosTestClient testClient = await CreateTestClientAsync();
            Container monitored = await testClient.CreateHierarchicalContainerAsync(HierarchicalContainerName, HierarchicalPartitionKeyPaths);
            Container lease = LeaseContainer(testClient);

            StandaloneCosmosOutboxRelayHostedService truncatedHost = RelayHost(monitored, lease, Path(HierarchicalPartitionKeyPaths[0]));

            Func<Task> startTruncated = () => truncatedHost.StartAsync(CancellationToken.None);

            (await startTruncated.Should().ThrowAsync<InvalidOperationException>(
                "declaring only the first segment of a hierarchical partition key does not match the container's real path"))
                .Which.Message.Should().Contain(HierarchicalPartitionKeyPaths[1],
                    "the violation names the container's ACTUAL second segment, which only the SDK's PartitionKeyPaths could supply");

            StandaloneCosmosOutboxRelayHostedService matchingHost = RelayHost(monitored, lease, HierarchicalPartitionKeyPaths);

            await StartAndStopAsync(matchingHost,
                "the full hierarchical declaration matches what the SDK reports, in order, segment for segment");
        }

        private Task<CosmosTestClient> CreateTestClientAsync()
            => CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);

        private static Container LeaseContainer(CosmosTestClient testClient)
            => testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.LeaseContainerName);

        // A standalone relay host over the supplied REAL containers. The declared source identities are unique per host
        // so the derived processor name never collides with another test's relay in the shared collection; the relay's
        // publish collaborators are stubs because no test here publishes — the contract is verified before any document
        // is drained.
        private static StandaloneCosmosOutboxRelayHostedService RelayHost(Container monitored,
                                                                         Container lease,
                                                                         IReadOnlyList<string> declaredPartitionKeyPath)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => monitored,
                    LeaseContainerFactory = _ => lease,
                    PartitionKeyPath = declaredPartitionKeyPath,
                    MonitoredSourceIdentity = "contract-monitored-" + Guid.NewGuid().ToString("N"),
                    LeaseSourceIdentity = "contract-lease-" + Guid.NewGuid().ToString("N"),
                });

        // Starts the host, asserting it does not fault, and always stops it so the started change-feed processor never
        // outlives the test that owns it.
        private static async Task StartAndStopAsync(StandaloneCosmosOutboxRelayHostedService host, string because)
        {
            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            try
            {
                await start.Should().NotThrowAsync(because);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }
        }

        // Writes one genuinely pending outbox document (the shape the relay publishes) at the container's real /pk path.
        private static async Task SeedPendingOutboxDocumentAsync(Container container, string partition)
        {
            string messageId = "msg-" + Guid.NewGuid().ToString("N");
            string destination = "contract-dest-" + Guid.NewGuid().ToString("N");

            using Stream document = OutboxControlDocument.ToStream(
                messageId, partition, destination, CosmosItemId.OutboxKind, CosmosOutboxDocument.StatusPending);
            using ResponseMessage response = await container.CreateItemStreamAsync(document, new PartitionKey(partition));

            response.IsSuccessStatusCode.Should().BeTrue(
                "the pending outbox document must exist before the host starts, so a host that did start would have had one to publish");
        }

        private static IReadOnlyList<string> Path(params string[] segments) => Array.AsReadOnly(segments);

        private static string UniquePartition() => "pk-" + Guid.NewGuid().ToString("N");
    }
}
