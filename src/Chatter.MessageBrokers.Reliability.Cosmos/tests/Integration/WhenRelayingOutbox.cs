using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
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
        // A non-outbox _chatterType for the discriminator-negative control (any value != CosmosItemId.OutboxKind that is
        // not the inbox kind either — a plausible domain discriminator that fails the relay's gate-1 _chatterType check).
        private const string NonOutboxDiscriminator = "domain-event";
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

            // Commit the POSITIVE doc through Chatter (handler follow-up Send on the framework batch). This is the only
            // fully-publishable outbox doc — the contrast the negative controls are measured against. It IS published.
            await harness.DeliverAsync(messageId, Command(aggregateId, partition, destination), ReceiverPath, PartitionProperty(partition));

            // Seed two SINGLE-VARIABLE negative controls in THIS test's partition. Each is byte-faithful to a real pending
            // outbox — id == ForOutbox(MessageId), all reconstruction fields publishable, MessageContext carrying the
            // capture infrastructure type + resolvable content type — so a doc that satisfied the relay's filter WOULD
            // reconstruct and publish to its UNIQUE sentinel Destination. Each varies EXACTLY ONE relay gate off that
            // publishable shape, so "the relay did not publish it" is attributable to precisely that gate (a relay that
            // ignored the varied gate but still required outbox shape can no longer skip it for the WRONG reason — e.g. a
            // missing reconstruction field — yielding a false green).
            //
            //   DISCRIMINATOR-negative: keeps id == ForOutbox(MessageId) (gate 3 passes) + status == pending (gate 2
            //   passes); varies ONLY _chatterType to a non-outbox value (gate 1 fails). Isolates the discriminator gate.
            string discriminatorMessageId = "msg-disc-" + Guid.NewGuid().ToString("N");
            string discriminatorSentinel = "relay-disc-sentinel-" + Guid.NewGuid().ToString("N");
            await SeedOutboxControlAsync(container, discriminatorMessageId, partition, discriminatorSentinel,
                chatterType: NonOutboxDiscriminator, status: CosmosOutboxDocument.StatusPending);

            //   STATUS-negative: keeps id == ForOutbox(MessageId) (gate 3 passes) + _chatterType == outbox (gate 1
            //   passes); varies ONLY status to delivered (gate 2 fails). Isolates the status gate.
            string statusMessageId = "msg-status-" + Guid.NewGuid().ToString("N");
            string statusSentinel = "relay-status-sentinel-" + Guid.NewGuid().ToString("N");
            await SeedOutboxControlAsync(container, statusMessageId, partition, statusSentinel,
                chatterType: CosmosItemId.OutboxKind, status: CosmosOutboxDocument.StatusDelivered);

            // Start the real relay; its change-feed processor drains the committed outbox backlog.
            await harness.StartAsync();

            // Exactly one publication to THIS test's unique destination (bounded wait).
            await WaitForPublishedToDestinationAsync(harness.Capture, destination, PublishTimeout);
            harness.Capture.Published.Count(m => m.Destination == destination)
                .Should().Be(1, "the committed outbox doc is published exactly once");

            // The outbox doc is marked delivered + carries a positive ttl (deterministic stamp, not gated on purge).
            await WaitForDeliveredWithTtlAsync(container, partition);

            // Neither single-variable control was published. Each control is byte-faithful to a publishable outbox doc
            // EXCEPT the one varied gate, so a publication to either sentinel Destination would mean the relay published a
            // doc it must skip — and because every OTHER field is publishable, the skip is attributable to exactly the
            // varied gate (discriminator or status), not to an incidental missing reconstruction field.
            harness.Capture.Published.Should().NotContain(m => m.Destination == discriminatorSentinel,
                "the discriminator-negative control varies ONLY _chatterType — the relay must skip it on gate 1 alone");
            harness.Capture.Published.Should().NotContain(m => m.Destination == statusSentinel,
                "the status-negative control varies ONLY status=delivered — the relay must skip it on gate 2 alone");
            harness.Capture.Published.Should().OnlyContain(m => !string.IsNullOrEmpty(m.Destination),
                "the relay must publish only well-formed outbox reconstructions (non-empty Destination)");

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

        // Writes a SINGLE-VARIABLE negative-control outbox document at the test edge: byte-faithful to a real pending
        // outbox (id == ForOutbox(MessageId), all reconstruction fields publishable, MessageContext carrying the capture
        // infrastructure type + resolvable content type) EXCEPT the one varied field (chatterType or status). Because the
        // doc is otherwise fully publishable, a relay that skipped it for any reason OTHER than the varied gate (e.g. a
        // missing reconstruction field) could not produce a false green — the control isolates exactly the one gate.
        private static async Task SeedOutboxControlAsync(Container container, string messageId, string partition, string sentinelDestination, string chatterType, string status)
        {
            using Stream stream = OutboxControlDocument.ToStream(messageId, partition, sentinelDestination, chatterType, status);
            using ResponseMessage response = await container.CreateItemStreamAsync(stream, new PartitionKey(partition));
            response.IsSuccessStatusCode.Should().BeTrue("the single-variable negative-control outbox doc must be created so the relay can be observed NOT publishing it");
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
