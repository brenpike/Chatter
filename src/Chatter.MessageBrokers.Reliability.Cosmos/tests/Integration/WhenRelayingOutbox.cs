using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // RELAY (criteria 3 + 4) driven through the REAL CosmosOutboxRelayHostedService against the emulator:
    //   - an outbox doc written THROUGH Chatter (a participant handler's follow-up Send committed on the framework
    //     batch) is published exactly once to the capturing sink, then marked status=delivered + a positive ttl
    //     (bounded-wait poll + edge read, never a fixed sleep);
    //   - a DOMAIN doc written via the edge client is NEVER published (the discriminator/id filter excludes it);
    //   - publish-once: the relay's own delivered/ttl update event does not republish (asserted by a stable single
    //     publication to the handler's destination over a settle window).
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenRelayingOutbox
    {
        private const string ReceiverPath = "primary-participant";
        // A destination NO legitimate outbox doc in this test uses. It is stamped onto the seeded NON-outbox documents so
        // that IF a broken relay republished a non-outbox change-feed event it would surface verbatim at this sentinel —
        // making the leak detectable by destination (the relay never maps a Cosmos doc id onto the reconstructed
        // MessageId, so a MessageId-based negative check could not catch the leak).
        private const string SentinelLeakDestination = "relay-leak-sentinel-must-never-publish";
        private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan DeliveredTimeout = TimeSpan.FromSeconds(120);
        // The bounded window over which a republish (the relay's own delivered/ttl change-feed event) must NOT appear.
        // This is a DEADLINE for a bounded poll that fails fast on a second publication — never a fixed settle sleep.
        private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(10);

        private readonly CosmosEmulatorFixture _emulator;

        public WhenRelayingOutbox(CosmosEmulatorFixture emulator) => _emulator = emulator;

        [RequiresDockerFact]
        public async Task PublishesOutboxDocumentOnceAndStampsDeliveredWithTtl()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container container = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);

            string partition = UniquePartition();
            string aggregateId = "agg-" + Guid.NewGuid().ToString("N");
            string messageId = "msg-" + Guid.NewGuid().ToString("N");
            // A UNIQUE destination per run isolates THIS test's publication from other tests' docs the shared
            // change-feed relay also drains from the collection's shared container.
            string destination = "relay-dest-" + Guid.NewGuid().ToString("N");

            await using CosmosReliabilityHarness harness = BuildHarness(testClient.Client);

            // Commit the outbox doc through Chatter (handler follow-up Send on the framework batch).
            await harness.DeliverAsync(messageId, Command(aggregateId, partition, destination), ReceiverPath, PartitionProperty(partition));

            // Seed two NON-outbox documents in THIS test's partition the relay must never publish:
            //   - a DOMAIN doc (no Chatter discriminator), and
            //   - a hand-authored INBOX marker (_chatterType="inbox") carrying a sentinel destination.
            // Both carry a sentinel Destination/payload that a broken relay republishing a non-outbox change-feed event
            // would surface verbatim into the capture sink — so the negative check is tied to a value a leak WOULD
            // produce, not to the Cosmos id (which the relay never maps onto the reconstructed MessageId).
            string domainId = "domain-" + Guid.NewGuid().ToString("N");
            await SeedDomainDocumentAsync(container, domainId, partition);
            await SeedInboxMarkerAsync(container, partition, SentinelLeakDestination);

            // Start the real relay; its change-feed processor drains the committed outbox backlog.
            await harness.StartAsync();

            // Exactly one publication to THIS test's unique destination (bounded wait).
            await WaitForPublishedToDestinationAsync(harness.Capture, destination, PublishTimeout);
            harness.Capture.Published.Count(m => m.Destination == destination)
                .Should().Be(1, "the committed outbox doc is published exactly once");

            // The outbox doc is marked delivered + carries a positive ttl (deterministic stamp, not gated on purge).
            await WaitForDeliveredWithTtlAsync(container, partition);

            // No domain doc and no inbox marker leaked into the publish ledger. A leaked NON-outbox publication cannot be
            // caught by MessageId (the relay never maps a Cosmos doc id onto the reconstructed MessageId), so the
            // discriminating checks are: (a) NOTHING was published to the sentinel destination the seeded inbox marker
            // carries, and (b) EVERY captured publication in THIS test's partition is a well-formed outbox reconstruction
            // (non-empty Destination) — a domain-doc leak reconstructs with a null/empty Destination and would fail this.
            harness.Capture.Published.Should().NotContain(m => m.Destination == SentinelLeakDestination,
                "a leaked inbox marker would republish its sentinel destination");
            harness.Capture.Published.Should().OnlyContain(m => !string.IsNullOrEmpty(m.Destination),
                "a domain document reconstructs with no Destination — the relay must publish only well-formed outbox docs");

            // Publish-once: the relay's own delivered/ttl update produces a change-feed event for a now-delivered doc; it
            // must NOT republish. Bounded poll: fail the moment a SECOND publication to our destination appears after the
            // delivered/ttl stamp was observed; otherwise return once the deadline passes with the count still at one.
            await AssertNoRepublishAsync(harness.Capture, destination, SettleWindow);
        }

        // Bounded poll until at least one message to the given destination is captured. Returns when seen, else throws
        // (a never-published publication fails fast rather than hanging).
        private static async Task WaitForPublishedToDestinationAsync(CapturingInfrastructure capture, string destination, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            do
            {
                if (capture.Published.Any(m => m.Destination == destination))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            while (DateTime.UtcNow < deadline);

            throw new Xunit.Sdk.XunitException($"No publication to destination '{destination}' was captured within {timeout}.");
        }

        // Writes a domain (aggregate-shaped) document with NO Chatter discriminator through the edge client; the relay's
        // discriminator/id filter must skip it.
        private static async Task SeedDomainDocumentAsync(Container container, string id, string partition)
        {
            using Stream stream = AggregateDocument.ToStream(id, partition, "domain-data");
            using ResponseMessage response = await container.CreateItemStreamAsync(stream, new PartitionKey(partition));
            response.IsSuccessStatusCode.Should().BeTrue("the domain document must be created so the relay can be observed NOT publishing it");
        }

        // Writes an INBOX-marker-shaped document (_chatterType="inbox") that is otherwise indistinguishable from a
        // publishable outbox doc — it carries status="pending" AND a Destination — so ONLY the discriminator filter
        // (_chatterType must equal "outbox") excludes it. A relay that filtered on anything weaker would republish it to
        // the sentinel destination, which the test asserts never happens. This drives the "never publish an inbox marker"
        // exclusion the prior assertion left untested.
        private static async Task SeedInboxMarkerAsync(Container container, string partition, string sentinelDestination)
        {
            var document = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = CosmosItemId.ForInbox("leak-" + Guid.NewGuid().ToString("N")),
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.InboxKind,
                [AggregateDocument.PartitionField] = partition,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.DestinationField] = sentinelDestination,
            };

            using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document), writable: false);
            using ResponseMessage response = await container.CreateItemStreamAsync(stream, new PartitionKey(partition));
            response.IsSuccessStatusCode.Should().BeTrue("the inbox marker must be created so the relay can be observed NOT publishing it");
        }

        // Bounded poll asserting NO republish: over the window, fail FAST the moment a SECOND publication to the
        // destination appears (the relay's own delivered/ttl change-feed event must not republish), otherwise return when
        // the deadline passes with the count still at one. This replaces a fixed settle sleep — it never sleeps the whole
        // window when a duplicate appears early, and the count check is re-polled until the deadline so a late duplicate
        // under slow CI is still caught.
        private static async Task AssertNoRepublishAsync(CapturingInfrastructure capture, string destination, TimeSpan window)
        {
            DateTime deadline = DateTime.UtcNow + window;
            do
            {
                int count = capture.Published.Count(m => m.Destination == destination);
                count.Should().BeLessThanOrEqualTo(1, "the relay's own delivered/ttl update must not republish");
                if (count > 1)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            while (DateTime.UtcNow < deadline);

            capture.Published.Count(m => m.Destination == destination)
                .Should().Be(1, "the relay's own delivered/ttl update must not republish");
        }

        // Bounded poll until the single outbox doc in the partition is status=delivered with a positive ttl (edge read,
        // not a fixed sleep). The outbox doc id is the handler-generated one (not predictable), so the doc is located by
        // its _chatterType=outbox discriminator within the partition.
        private static async Task WaitForDeliveredWithTtlAsync(Container container, string partition)
        {
            DateTime deadline = DateTime.UtcNow + DeliveredTimeout;
            string lastStatus = null;
            do
            {
                (string status, bool positiveTtl) = await ReadOutboxStatusAsync(container, partition);
                lastStatus = status;
                if (string.Equals(status, CosmosOutboxDocument.StatusDelivered, StringComparison.Ordinal) && positiveTtl)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            while (DateTime.UtcNow < deadline);

            throw new Xunit.Sdk.XunitException(
                $"The outbox document was not stamped delivered with a positive ttl within {DeliveredTimeout} (last observed status '{lastStatus}').");
        }

        // Reads the single outbox doc's status + whether it carries a positive ttl. The doc id is located by a
        // VALUE-string projection (safe across the feed page lifetime, unlike a JsonElement projection), then the doc is
        // point-read as a stream the test parses with System.Text.Json (the stream bytes are fully test-owned, avoiding
        // the disposed-JsonElement hazard of GetItemQueryIterator<JsonElement>).
        private static async Task<(string status, bool positiveTtl)> ReadOutboxStatusAsync(Container container, string partition)
        {
            var query = new QueryDefinition($"SELECT VALUE c.{CosmosOutboxDocument.IdField} FROM c WHERE c[\"{CosmosOutboxDocument.DiscriminatorField}\"] = @type")
                .WithParameter("@type", CosmosItemId.OutboxKind);

            string outboxId = null;
            using (FeedIterator<string> iterator = container.GetItemQueryIterator<string>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partition) }))
            {
                while (iterator.HasMoreResults && outboxId is null)
                {
                    FeedResponse<string> page = await iterator.ReadNextAsync();
                    foreach (string id in page)
                    {
                        outboxId = id;
                        break;
                    }
                }
            }

            if (outboxId is null)
            {
                return (null, false);
            }

            using ResponseMessage read = await container.ReadItemStreamAsync(outboxId, new PartitionKey(partition));
            if (read.StatusCode != HttpStatusCode.OK)
            {
                return (null, false);
            }

            using JsonDocument document = await JsonDocument.ParseAsync(read.Content);
            JsonElement root = document.RootElement;
            string status = root.TryGetProperty(CosmosOutboxDocument.StatusField, out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            bool positiveTtl = root.TryGetProperty("ttl", out JsonElement ttlElement)
                && ttlElement.ValueKind == JsonValueKind.Number
                && ttlElement.GetInt32() > 0;
            return (status, positiveTtl);
        }

        private static CosmosReliabilityHarness BuildHarness(CosmosClient client)
            => CosmosReliabilityHarness.Build(
                client,
                pipeline => pipeline.WithCosmosDocumentReliability<PrimaryParticipantCommand>(
                    CosmosTestClient.DatabaseName,
                    CosmosTestClient.DocumentContainerName,
                    CosmosTestClient.LeaseContainerName,
                    TestResolvers.ResolvePartition,
                    CosmosTestClient.PartitionKeyPath),
                services => services.AddTransient<IMessageHandler<PrimaryParticipantCommand>, PrimaryParticipantHandler>());

        private static PrimaryParticipantCommand Command(string aggregateId, string partition, string destination)
            => new PrimaryParticipantCommand { AggregateId = aggregateId, Partition = partition, Payload = "relay", OutboundDestination = destination, OutboundMessageId = "out-" + Guid.NewGuid().ToString("N") };

        private static IDictionary<string, object> PartitionProperty(string partition)
            => new Dictionary<string, object> { [TestResolvers.PartitionProperty] = partition };

        private static string UniquePartition() => "pk-" + Guid.NewGuid().ToString("N");
    }
}
