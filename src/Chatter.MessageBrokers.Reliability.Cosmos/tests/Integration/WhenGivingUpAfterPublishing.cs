using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // The post-publish brake and its start-time companion guard, proven against REAL emulator containers rather than a
    // Mock<Container>. The unit suite (UsingStandaloneCosmosOutboxRelayHost/WhenGivingUpAfterPublishing,
    // UsingCosmosOutboxRelayHost/WhenGivingUpAfterPublishing and UsingMonitoredContainerContract) already owns every
    // assertion that must protect CI — the phase election, the sticky published flag, the single status op, the absent
    // ttl op, the batch continuation. What only a REAL container can add is that the two Cosmos-side facts those fakes
    // are built on are true of the live service:
    //   1. a container CAN permanently reject the relay's two-op delivered stamp (status + ttl) while still accepting
    //      the one-op status stamp the give-up issues — the asymmetry the whole brake depends on, faked in the unit
    //      suite by a Moq callback that faults on the ttl op; and
    //   2. the SDK really reports a container's partition-key path back in ContainerProperties.PartitionKeyPaths, so a
    //      container partitioned on a relay-stamped path is caught at start rather than at the first failed stamp.
    //
    // Each test provisions its OWN container and its OWN logical partition so nothing here observes, or is observed by,
    // another test's documents in the shared collection.
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenGivingUpAfterPublishing
    {
        // The monitored container of the brake test. It SATISFIES the start-time contract (the suite's /pk path, no
        // default time-to-live) and still cannot accept the delivered stamp — see CreateContainerRejectingDeliveredTtlAsync.
        private const string UnconfirmableContainerName = "give-up-unconfirmable";
        // A container partitioned on the relay-stamped '/ttl' path: the field misconfiguration the start-time contract
        // refuses.
        private const string TtlPartitionedContainerName = "give-up-ttl-partitioned";

        // The ttl the relay stamps on a delivered document here. A distinctive value (not the 86400 default) so the
        // claim document that occupies it in the unique-key index is unmistakably deliberate.
        private const int DeliveredTtlSeconds = 4242;

        // The lease token the change-feed processor would pass the drain callback. Only ever logged/attributed here —
        // no lease container is involved because the batches are handed to the callback directly.
        private const string LeaseToken = "give-up-lease-0";

        // The cap is the one a consumer who configured NOTHING gets, so the brake proven here is the out-of-the-box one.
        private const int UnconfirmedPublishCap = OutboxGiveUpPolicy.DefaultGiveUpAfterUnconfirmedPublishes;

        private static readonly IReadOnlyList<string> DeclaredPartitionKeyPath = Array.AsReadOnly(new[] { CosmosTestClient.PartitionKeyPath });
        private static readonly IReadOnlyList<string> TtlPartitionKeyPath = Array.AsReadOnly(new[] { "/" + CosmosOutboxDocument.TtlField });

        private readonly CosmosEmulatorFixture _emulator;

        public WhenGivingUpAfterPublishing(CosmosEmulatorFixture emulator) => _emulator = emulator;

        // THE BRAKE, against a real container and a real document. The delivered stamp fails on every pass for a reason
        // Cosmos itself enforces, the message goes out once per pass, and at the cap the REAL document is advanced to
        // published-unconfirmed: still there, carrying no ttl, and no longer admitted by the pending gate — so the
        // change feed advances past it instead of publishing it again forever.
        [RequiresDockerFact]
        public async Task StampsTheRealDocumentPublishedUnconfirmedOnceTheCapIsReached()
        {
            await using CosmosTestClient testClient = await CreateTestClientAsync();
            Container monitored = await CreateContainerRejectingDeliveredTtlAsync(testClient);

            string partition = UniquePartition();
            await ClaimTheDeliveredTtlAsync(monitored, partition);

            string messageId = "give-up-msg-" + Guid.NewGuid().ToString("N");
            string destination = "give-up-dest-" + Guid.NewGuid().ToString("N");
            await SeedPendingOutboxDocumentAsync(monitored, messageId, partition, destination);

            string outboxId = CosmosItemId.ForOutbox(messageId);
            var capture = new CapturingInfrastructure();
            StandaloneCosmosOutboxRelayHostedService host = RelayHost(monitored, LeaseContainer(testClient), DeclaredPartitionKeyPath, capture);

            // Below the cap the relay stays fail-closed: the real delivered stamp fails, the failure propagates, and the
            // document is left pending for the next pass.
            foreach (int pass in Enumerable.Range(1, UnconfirmedPublishCap - 1))
            {
                JsonElement stored = await ReadStoredDocumentAsync(monitored, outboxId, partition);
                Func<Task> drain = () => DrainAsync(host, monitored, stored);

                await drain.Should().ThrowAsync<CosmosException>(
                    $"pass {pass} is below the cap, so a delivered stamp the container refuses is propagated rather than absorbed");

                CosmosOutboxDocument.IsPendingOutbox(await ReadStoredDocumentAsync(monitored, outboxId, partition))
                    .Should().BeTrue($"the document is still pending after pass {pass} — nothing has given up on it yet");
            }

            JsonElement atTheCap = await ReadStoredDocumentAsync(monitored, outboxId, partition);
            Func<Task> cappedDrain = () => DrainAsync(host, monitored, atTheCap);

            await cappedDrain.Should().NotThrowAsync(
                "at the cap the relay stops re-publishing, so the batch checkpoints instead of re-surfacing the document forever");

            capture.Published.Count(message => message.Destination == destination)
                .Should().Be(UnconfirmedPublishCap,
                    "the message went out once per pass, and the cap is what stops an unbounded number of them");

            JsonElement givenUp = await ReadStoredDocumentAsync(monitored, outboxId, partition);

            givenUp.GetProperty(CosmosOutboxDocument.StatusField).GetString().Should().Be(CosmosOutboxDocument.StatusUnconfirmed,
                "the message DID reach the broker, so the real document records a delivery nobody could confirm — never one that was never delivered");
            givenUp.TryGetProperty(CosmosOutboxDocument.TtlField, out _).Should().BeFalse(
                "a document nobody could confirm is evidence: it is never scheduled for self-purge, so the real document carries no ttl at all");
            CosmosOutboxDocument.IsPendingOutbox(givenUp).Should().BeFalse(
                "the stamp advanced the real document out of pending, so the change feed advances past it");

            // The feed really does advance: handed the document AS IT NOW STANDS IN COSMOS, the relay publishes nothing
            // more. This is the assertion the whole brake exists for — the republish loop is over, not merely capped.
            Func<Task> afterTheCap = () => DrainAsync(host, monitored, givenUp);

            await afterTheCap.Should().NotThrowAsync("a published-unconfirmed document is skipped, not drained");
            capture.Published.Count(message => message.Destination == destination)
                .Should().Be(UnconfirmedPublishCap, "nothing publishes the given-up document again");
        }

        // The one assertion no mock can prove: the SDK really reports the container's own partition-key path back, so a
        // container partitioned on a path the relay patches on every drain is refused at START — before a single
        // document is published against a stamp that could never land.
        [RequiresDockerFact]
        public async Task RefusesToStartOverAContainerPartitionedOnAStampedPath()
        {
            await using CosmosTestClient testClient = await CreateTestClientAsync();
            Container monitored = await testClient.CreateHierarchicalContainerAsync(TtlPartitionedContainerName, TtlPartitionKeyPath);

            // The declared path MATCHES the container's real one, so the partition-key check passes and the stamped-path
            // collision is the ONLY violation the start can be failing on.
            StandaloneCosmosOutboxRelayHostedService host = RelayHost(monitored, LeaseContainer(testClient), TtlPartitionKeyPath, new CapturingInfrastructure());

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            string violation = (await start.Should().ThrowAsync<InvalidOperationException>(
                "a container partitioned on '/ttl' can never accept the delivered stamp, so the relay refuses to begin the publish-then-fail loop"))
                .Which.Message;

            violation.Should().Contain("/" + CosmosOutboxDocument.TtlField,
                "the violation names the stamped path the container is partitioned on — a string only the SDK's PartitionKeyPaths could have supplied");
            violation.Should().NotContain("does not match the container's actual partition-key path",
                "the declared path matches, so the refusal is the stamped-path collision alone");
        }

        private Task<CosmosTestClient> CreateTestClientAsync()
            => CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);

        private static Container LeaseContainer(CosmosTestClient testClient)
            => testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.LeaseContainerName);

        // A REAL container that permanently rejects the relay's DELIVERED stamp while still accepting its GIVE-UP stamp
        // — the asymmetry the brake is built on, produced by Cosmos rather than by a mock.
        //
        // The delivered stamp is two ops (status + ttl); the give-up stamp is one (status). A UNIQUE KEY on '/ttl'
        // separates them: once ClaimTheDeliveredTtlAsync has taken the ttl value the relay stamps within the logical
        // partition, every delivered stamp is rejected 409 Conflict ("Document with unique key already exists") and, a
        // patch being atomic, the status op in it never lands either — while the status-only give-up patch is accepted.
        // The rejection is a property of the container, so it is permanent and needs no test seam at all.
        //
        // The container is otherwise ORDINARY and contract-satisfying (the suite's single-segment /pk path, no default
        // time-to-live), which is the point: the container the OTHER test provisions — partitioned on '/ttl', the field
        // shape of this defect — is now refused at start, so proving the runtime brake needs a container whose stamp
        // failure the start-time contract cannot foresee.
        private static async Task<Container> CreateContainerRejectingDeliveredTtlAsync(CosmosTestClient testClient)
        {
            var properties = new ContainerProperties(UnconfirmableContainerName, CosmosTestClient.PartitionKeyPath);
            properties.UniqueKeyPolicy.UniqueKeys.Add(new UniqueKey { Paths = { "/" + CosmosOutboxDocument.TtlField } });

            ContainerResponse response = await testClient.Client
                .GetDatabase(CosmosTestClient.DatabaseName)
                .CreateContainerIfNotExistsAsync(properties);

            return response.Container;
        }

        // Takes the ttl value the relay stamps on delivery, inside the test's own logical partition, so the unique-key
        // index refuses the relay's delivered stamp from the first pass onward.
        private static async Task ClaimTheDeliveredTtlAsync(Container container, string partition)
        {
            string claim = $"{{\"id\":\"delivered-ttl-claim-{Guid.NewGuid():N}\",\"pk\":\"{partition}\",\"{CosmosOutboxDocument.TtlField}\":{DeliveredTtlSeconds}}}";

            using var body = new MemoryStream(Encoding.UTF8.GetBytes(claim), writable: false);
            using ResponseMessage response = await container.CreateItemStreamAsync(body, new PartitionKey(partition));

            response.IsSuccessStatusCode.Should().BeTrue(
                "the delivered ttl must be claimed before the relay drains, or the delivered stamp would succeed and there would be no unconfirmed publish to brake");
        }

        // Writes one genuinely pending outbox document — the shape the relay publishes — at the container's real /pk path.
        private static async Task SeedPendingOutboxDocumentAsync(Container container, string messageId, string partition, string destination)
        {
            using Stream document = OutboxControlDocument.ToStream(
                messageId, partition, destination, CosmosItemId.OutboxKind, CosmosOutboxDocument.StatusPending);
            using ResponseMessage response = await container.CreateItemStreamAsync(document, new PartitionKey(partition));

            response.IsSuccessStatusCode.Should().BeTrue("the pending outbox document must exist before the relay drains it");
        }

        // Hands the relay ONE change-feed batch carrying the document exactly as it stands in Cosmos — the same payload
        // shape, and the same drain callback, the change-feed processor invokes. The batch is fed directly rather than
        // through a started processor because the failure under test must repeat a DETERMINISTIC number of times: the
        // cap is a per-document counter, so the number of passes has to be the test's to control.
        private static async Task DrainAsync(StandaloneCosmosOutboxRelayHostedService host, Container monitored, JsonElement storedDocument)
        {
            using var batch = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"Documents\":[{storedDocument.GetRawText()}]}}"), writable: false);
            await host.HandleChangesAsync(batch, monitored, DeclaredPartitionKeyPath, LeaseToken, CancellationToken.None);
        }

        // Point-reads the stored document and returns a CLONE of its root element: the response stream is disposed here,
        // so a non-cloned JsonElement would be read after its owning document was released.
        private static async Task<JsonElement> ReadStoredDocumentAsync(Container container, string id, string partition)
        {
            using ResponseMessage read = await container.ReadItemStreamAsync(id, new PartitionKey(partition));

            read.StatusCode.Should().Be(HttpStatusCode.OK,
                "the document the relay gives up on is evidence and stays inspectable — it is never deleted");

            using JsonDocument document = await JsonDocument.ParseAsync(read.Content);
            return document.RootElement.Clone();
        }

        // A standalone relay host over the supplied REAL container handles, publishing through the capture sink via
        // Chatter's own infrastructure provider and body-converter factory (no dispatch mock — the publish half of the
        // two-step send is real). The declared source identities are unique per host so the derived processor name never
        // joins another relay's consumer group in the shared collection, and the poison policy is left OFF so the brake
        // exercised here is the always-on post-publish one.
        private static StandaloneCosmosOutboxRelayHostedService RelayHost(Container monitored,
                                                                         Container lease,
                                                                         IReadOnlyList<string> declaredPartitionKeyPath,
                                                                         CapturingInfrastructure capture)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                new MessagingInfrastructureProvider(new[] { (IMessagingInfrastructure)capture }, NullLogger<MessagingInfrastructureProvider>.Instance),
                new BodyConverterFactory(new IBrokeredMessageBodyConverter[] { new JsonBodyConverter() }),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => monitored,
                    LeaseContainerFactory = _ => lease,
                    PartitionKeyPath = declaredPartitionKeyPath,
                    DeliveredTtlSeconds = DeliveredTtlSeconds,
                    MonitoredSourceIdentity = "give-up-monitored-" + Guid.NewGuid().ToString("N"),
                    LeaseSourceIdentity = "give-up-lease-" + Guid.NewGuid().ToString("N"),
                });

        private static string UniquePartition() => "pk-" + Guid.NewGuid().ToString("N");
    }
}
