using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // INBOX IDEMPOTENCY (criterion 2 + architecture-update-1) driven through the public receive path and asserted at
    // the FRAMEWORK-OWNED batch-execute commit point:
    //   - DUPLICATE IDENTITY: the same MessageId delivered twice commits the aggregate + outbox exactly once. The first
    //     delivery commits; the redelivery's co-resident inbox marker create 409s at the framework batch-execute and the
    //     confirmed-duplicate path acks WITHOUT committing a second aggregate or outbox doc.
    //   - FORCED INBOX-MARKER 409: pre-seeding a genuine inbox:{encoded(MessageId)} marker (correct _chatterType=inbox,
    //     matching MessageId, the encoded inbox: id) then delivering once forces the marker create to 409 at the
    //     framework batch-execute on the FIRST delivery — the confirmed-duplicate path leaves NO aggregate write and NO
    //     outbox record. This asserts the framework-owned commit point (NOT a handler-internal execute, NOT an EF-shaped
    //     TransactionContext/InboxBehavior seam).
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenDeduplicatingInbox
    {
        private const string ReceiverPath = "primary-participant";
        private const string OutboundDestination = "downstream-orders";
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

        private readonly CosmosEmulatorFixture _emulator;

        public WhenDeduplicatingInbox(CosmosEmulatorFixture emulator) => _emulator = emulator;

        [RequiresDockerFact]
        public async Task DuplicateIdentityIsHandledOnce()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container container = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);

            string partition = UniquePartition();
            string firstAggregateId = "agg-" + Guid.NewGuid().ToString("N");
            string messageId = "msg-" + Guid.NewGuid().ToString("N");

            await using CosmosReliabilityHarness harness = BuildHarness(testClient.Client);

            // First delivery commits the aggregate + the co-resident outbox + the inbox marker.
            await harness.DeliverAsync(messageId, Command(firstAggregateId, partition, "first"), ReceiverPath, PartitionProperty(partition));
            (await CosmosEdge.WaitForPresenceAsync(container, firstAggregateId, partition, expectPresent: true, ReadTimeout))
                .Should().BeTrue("the first delivery commits the aggregate");
            (await CosmosEdge.WaitForCountByChatterTypeAsync(container, CosmosItemId.OutboxKind, partition, minCount: 1, ReadTimeout))
                .Should().Be(1, "the first delivery commits exactly one outbox doc");

            // The redelivery's marker create 409s at the framework batch-execute; the confirmed-duplicate path acks
            // without re-committing. A different aggregate id on the duplicate proves no SECOND aggregate is written, and
            // the outbox/inbox counts stay at one.
            string secondAggregateId = "agg-" + Guid.NewGuid().ToString("N");
            await harness.DeliverAsync(messageId, Command(secondAggregateId, partition, "second"), ReceiverPath, PartitionProperty(partition));

            (await CosmosEdge.WaitForPresenceAsync(container, secondAggregateId, partition, expectPresent: false, ReadTimeout))
                .Should().BeFalse("a redelivered duplicate must NOT commit a second aggregate write");
            (await CosmosEdge.CountByChatterTypeAsync(container, CosmosItemId.OutboxKind, partition))
                .Should().Be(1, "a redelivered duplicate must NOT commit a second outbox doc");
            (await CosmosEdge.CountByChatterTypeAsync(container, CosmosItemId.InboxKind, partition))
                .Should().Be(1, "the inbox marker is written exactly once for the identity");
        }

        [RequiresDockerFact]
        public async Task PreSeededMarkerForcesConfirmedDuplicateWithNoWrites()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container container = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);

            string partition = UniquePartition();
            string aggregateId = "agg-" + Guid.NewGuid().ToString("N");
            string messageId = "msg-" + Guid.NewGuid().ToString("N");

            // Pre-seed a GENUINE inbox marker (the exact wire shape the framework writes) for this MessageId/partition so
            // the upcoming first delivery's marker create 409s and the confirm-read swallows it as a duplicate.
            await SeedInboxMarkerAsync(container, messageId, partition);

            await using CosmosReliabilityHarness harness = BuildHarness(testClient.Client);

            await harness.DeliverAsync(messageId, Command(aggregateId, partition, "shadowed"), ReceiverPath, PartitionProperty(partition));

            // The confirmed-duplicate path acked without committing: no aggregate write, no outbox record.
            (await CosmosEdge.WaitForPresenceAsync(container, aggregateId, partition, expectPresent: false, ReadTimeout))
                .Should().BeFalse("a pre-seeded marker forces the confirmed-duplicate path — NO aggregate write");
            (await CosmosEdge.CountByChatterTypeAsync(container, CosmosItemId.OutboxKind, partition))
                .Should().Be(0, "a pre-seeded marker forces the confirmed-duplicate path — NO outbox record");
        }

        // Writes the genuine inbox marker wire document for messageId at the partition, mirroring exactly what the
        // framework's TryStampInboxMarker stages (CosmosInboxMarker rendered with the partition value stamped at the
        // declared PK path). Written through the edge client so the framework's marker create later conflicts with it.
        private static async Task SeedInboxMarkerAsync(Container container, string messageId, string partition)
        {
            CosmosInboxMarker marker = CosmosInboxMarker.From(messageId);
            var partitionKeyValues = new List<JsonElement> { JsonElementOf(partition) };
            JsonObject rendered = marker.ToJsonObject(new[] { CosmosTestClient.PartitionKeyPath }, partitionKeyValues);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(rendered);

            using var stream = new MemoryStream(bytes, writable: false);
            using ResponseMessage response = await container.CreateItemStreamAsync(stream, new PartitionKey(partition));
            response.IsSuccessStatusCode.Should().BeTrue("the pre-seeded marker must be created so the framework marker-create later conflicts with it");
        }

        private static JsonElement JsonElementOf(string raw)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(raw));
            return document.RootElement.Clone();
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

        private static PrimaryParticipantCommand Command(string aggregateId, string partition, string payload)
            => new PrimaryParticipantCommand { AggregateId = aggregateId, Partition = partition, Payload = payload, OutboundDestination = OutboundDestination, OutboundMessageId = "out-" + Guid.NewGuid().ToString("N") };

        private static IDictionary<string, object> PartitionProperty(string partition)
            => new Dictionary<string, object> { [TestResolvers.PartitionProperty] = partition };

        private static string UniquePartition() => "pk-" + Guid.NewGuid().ToString("N");
    }
}
