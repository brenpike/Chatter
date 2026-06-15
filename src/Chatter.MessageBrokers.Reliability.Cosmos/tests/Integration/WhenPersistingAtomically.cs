using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // ATOMICITY (criterion 1) driven through Chatter's public receive path and observed through edge reads of the
    // emulator:
    //   - SUCCESS: a participant handler stages its aggregate AND a follow-up Send (the co-resident outbox doc) on the
    //     framework-owned batch; on commit BOTH the aggregate AND an outbox doc are present in the partition.
    //   - FORCED CONCURRENCY FAILURE: the handler replaces the pre-seeded aggregate with a stale IfMatchEtag so the
    //     framework batch fails at execute (412); because the batch is all-or-nothing, NO outbox doc commits and the
    //     aggregate is NOT mutated.
    //
    // No test asserts a hand-built SDK batch as the system under test: the batch is opened/executed by the framework
    // behavior; the test only delivers a command and reads the committed documents at the edge. Outbox-doc presence is
    // asserted by COUNTING _chatterType=outbox docs in the partition (the handler-generated outbox id is not predictable
    // from the test — SendOptions.MessageId does not survive the core handler-context Send merge).
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenPersistingAtomically
    {
        private const string ReceiverPath = "primary-participant";
        private const string OutboundDestination = "downstream-orders";
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

        private readonly CosmosEmulatorFixture _emulator;

        public WhenPersistingAtomically(CosmosEmulatorFixture emulator) => _emulator = emulator;

        [RequiresDockerFact]
        public async Task SuccessCommitsAggregateAndOutboxTogether()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container container = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);

            string partition = UniquePartition();
            string aggregateId = "agg-" + Guid.NewGuid().ToString("N");
            string messageId = "msg-" + Guid.NewGuid().ToString("N");

            await using CosmosReliabilityHarness harness = BuildHarness(testClient.Client);

            await harness.DeliverAsync(
                messageId,
                Command(aggregateId, partition, "ok"),
                ReceiverPath,
                PartitionProperty(partition));

            // The aggregate committed.
            (await CosmosEdge.WaitForPresenceAsync(container, aggregateId, partition, expectPresent: true, ReadTimeout))
                .Should().BeTrue("the aggregate must commit on success");
            // The co-resident outbox doc committed atomically (asserted by partition-scoped count).
            (await CosmosEdge.WaitForCountByChatterTypeAsync(container, CosmosItemId.OutboxKind, partition, minCount: 1, ReadTimeout))
                .Should().Be(1, "exactly one co-resident outbox doc must commit atomically with the aggregate");

            // The co-resident outbox doc carries the Chatter-owned WIRE SHAPE. The exact-id assertion
            // (outbox:{encoded(the-supplied-MessageId)}) is BLOCKED by the product quirk that SendOptions.MessageId does
            // not survive the handler-context Send merge in the core library (tracked as issue #245), so the id is asserted
            // by its reserved outbox: prefix rather than the exact encoded MessageId; the prefix/shape assertion is the
            // tightest available until #245 is resolved.
            CosmosEdge.OutboxWireShape? shape = await CosmosEdge.ReadOutboxWireShapeAsync(container, partition);
            shape.Should().NotBeNull("the co-resident outbox doc must be readable at the edge");
            CosmosEdge.OutboxWireShape outbox = shape.Value;

            CosmosItemId.IsReserved(outbox.Id).Should().BeTrue("the outbox doc id is in the Chatter-reserved id namespace");
            outbox.Id.Should().StartWith(CosmosItemId.OutboxKind + ":", "the outbox doc id carries the reserved outbox: prefix");
            outbox.ChatterType.Should().Be(CosmosItemId.OutboxKind, "the discriminator must mark the doc as an outbox doc");
            outbox.Status.Should().Be(CosmosOutboxDocument.StatusPending, "the relay is not started, so the outbox doc stays pending");
            outbox.Destination.Should().Be(OutboundDestination, "the outbox doc carries the handler's outbound destination");
            outbox.MessageBody.Should().NotBeNullOrEmpty("the outbox doc carries the serialized follow-up body");
            outbox.MessageContentType.Should().NotBeNullOrEmpty("the outbox doc carries a resolvable content type");
            outbox.HasMessageContext.Should().BeTrue("the outbox doc carries the serialized message context");
        }

        [RequiresDockerFact]
        public async Task ForcedAggregateConcurrencyFailureCommitsNeither()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container container = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);

            string partition = UniquePartition();
            string aggregateId = "agg-" + Guid.NewGuid().ToString("N");
            string messageId = "msg-" + Guid.NewGuid().ToString("N");

            // Pre-seed the aggregate so the handler's stale-ETag REPLACE targets an existing item and yields a clean 412.
            await SeedAggregateAsync(container, aggregateId, partition, "seed");

            await using CosmosReliabilityHarness harness = BuildHarness(
                testClient.Client,
                services => services.AddTransient<IMessageHandler<PrimaryParticipantCommand>, ConflictParticipantHandler>());

            // The forced 412 surfaces as a CosmosBatchExecutionException out of the framework batch-execute; the receive
            // path propagates it (no ack). The test asserts the throw AND the absence of any outbox write.
            Func<Task> deliver = () => harness.DeliverAsync(
                messageId,
                Command(aggregateId, partition, "conflict"),
                ReceiverPath,
                PartitionProperty(partition));

            await deliver.Should().ThrowAsync<CosmosBatchExecutionException>(
                "a stale IfMatchEtag replace forces the framework batch-execute to fail the precondition (412)");

            // The aggregate is unchanged (still the seeded payload) and NO outbox doc committed (all-or-nothing).
            (await ReadAggregatePayloadAsync(container, aggregateId, partition))
                .Should().Be("seed", "a failed batch must NOT mutate the aggregate");
            (await CosmosEdge.CountByChatterTypeAsync(container, CosmosItemId.OutboxKind, partition))
                .Should().Be(0, "a failed batch must NOT commit the outbox doc (all-or-nothing)");
        }

        private static CosmosReliabilityHarness BuildHarness(CosmosClient client, Action<IServiceCollection> configureServices = null)
            => CosmosReliabilityHarness.Build(
                client,
                pipeline => pipeline.WithCosmosDocumentReliability<PrimaryParticipantCommand>(
                    CosmosTestClient.DatabaseName,
                    CosmosTestClient.DocumentContainerName,
                    CosmosTestClient.LeaseContainerName,
                    TestResolvers.ResolvePartition,
                    CosmosTestClient.PartitionKeyPath),
                services =>
                {
                    services.AddTransient<IMessageHandler<PrimaryParticipantCommand>, PrimaryParticipantHandler>();
                    configureServices?.Invoke(services);
                });

        private static async Task SeedAggregateAsync(Container container, string aggregateId, string partition, string payload)
        {
            using Stream stream = AggregateDocument.ToStream(aggregateId, partition, payload);
            using ResponseMessage response = await container.CreateItemStreamAsync(stream, new PartitionKey(partition));
            response.IsSuccessStatusCode.Should().BeTrue("the aggregate must be pre-seeded so the stale-ETag replace yields a clean 412");
        }

        private static async Task<string> ReadAggregatePayloadAsync(Container container, string aggregateId, string partition)
        {
            using ResponseMessage read = await container.ReadItemStreamAsync(aggregateId, new PartitionKey(partition));
            if (read.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return null;
            }

            using System.Text.Json.JsonDocument document = await System.Text.Json.JsonDocument.ParseAsync(read.Content);
            return document.RootElement.TryGetProperty(AggregateDocument.PayloadField, out System.Text.Json.JsonElement payload)
                ? payload.GetString()
                : null;
        }

        private static PrimaryParticipantCommand Command(string aggregateId, string partition, string payload)
            => new PrimaryParticipantCommand { AggregateId = aggregateId, Partition = partition, Payload = payload, OutboundDestination = OutboundDestination, OutboundMessageId = "out-" + Guid.NewGuid().ToString("N") };

        private static IDictionary<string, object> PartitionProperty(string partition)
            => new Dictionary<string, object> { [TestResolvers.PartitionProperty] = partition };

        private static string UniquePartition() => "pk-" + Guid.NewGuid().ToString("N");
    }
}
