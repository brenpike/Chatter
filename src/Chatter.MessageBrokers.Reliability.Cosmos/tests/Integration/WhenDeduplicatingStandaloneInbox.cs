using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // STANDALONE, lease-less Cosmos inbox (#253, ADR-0009) driven end-to-end through the REAL InboxBehavior<T> seam and
    // the REAL WithCosmosInbox registration path against the live emulator:
    //   - FIRST DELIVERY runs the handler exactly once and the write-ahead claim leaves a genuine inbox marker on the
    //     /idempotencyKey container.
    //   - A REDELIVERY of the SAME identity hits a real 409-on-create; the mandatory confirm read-back point-reads the
    //     conflicting marker (immediately visible under session consistency on the same client), converges, and SUPPRESSES
    //     the duplicate — the handler is NOT re-invoked and no exception propagates.
    //   - The ASSEMBLED pipeline registers NO IHostedService (no relay host, no lease processor, no outbox poller) and no
    //     Cosmos outbox: WithCosmosInbox stands alone as the once-only gate (ADR-0009 D3, contrast with the document tier).
    //
    // Unlike the document-tier suite (WhenDeduplicatingInbox) this opens NO TransactionalBatch and asserts at the
    // InboxBehavior seam, not the framework-owned batch-execute commit point.
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenDeduplicatingStandaloneInbox
    {
        private const string ReceiverPath = "standalone-consumer";

        // A dedicated /idempotencyKey-partitioned marker container, distinct from the document-tier containers
        // CosmosTestClient provisions (which partition on /pk). The standalone inbox partitions each marker by the inbound
        // message id at this single-segment path.
        private const string InboxContainerName = "standalone-inbox";
        private const string InboxPartitionKeyPath = "/idempotencyKey";

        private readonly CosmosEmulatorFixture _emulator;

        public WhenDeduplicatingStandaloneInbox(CosmosEmulatorFixture emulator) => _emulator = emulator;

        [RequiresDockerFact]
        public async Task RedeliveredIdentityRunsHandlerOnceAndWritesTheMarkerExactlyOnce()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            await EnsureInboxContainerAsync(testClient.Client);
            Container inboxContainer = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, InboxContainerName);

            string messageId = "msg-" + Guid.NewGuid().ToString("N");
            var invocations = new HandlerInvocationCounter();

            await using CosmosReliabilityHarness harness = BuildHarness(testClient.Client, invocations);

            // First delivery: the write-ahead claim creates the marker (201) then runs the handler exactly once.
            await harness.DeliverAsync(messageId, new StandaloneInboxCommand { Payload = "first" }, ReceiverPath);

            invocations.Count.Should().Be(1, "the first delivery of a fresh identity runs the handler exactly once");

            using (ResponseMessage marker = await inboxContainer.ReadItemStreamAsync(
                CosmosItemId.ForInbox(messageId), new PartitionKey(messageId)))
            {
                marker.IsSuccessStatusCode.Should().BeTrue(
                    "the first delivery's write-ahead claim leaves a genuine inbox marker on the /idempotencyKey container");
            }

            // Redelivery of the SAME identity: the marker create 409s, the confirm read-back converges on the genuine
            // marker under session consistency, and the handler is SUPPRESSED (not re-invoked) with no exception thrown.
            await harness.DeliverAsync(messageId, new StandaloneInboxCommand { Payload = "duplicate" }, ReceiverPath);

            invocations.Count.Should().Be(1,
                "a redelivered duplicate is suppressed after the confirm read-back converges — the handler is NOT re-invoked");
        }

        [RequiresDockerFact]
        public async Task AssembledPipelineRegistersNoHostedServiceOutboxOrLease()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);

            // Assemble the FULL DI graph the standalone consumer stands up (AddChatterCqrs + AddMessageBrokers +
            // WithCosmosInbox against the app-owned CosmosClient), then assert what it did and did NOT register.
            var services = new ServiceCollection();
            services.AddLogging();
            services
                .AddChatterCqrs(new ConfigurationBuilder().Build(),
                    pipeline => pipeline.WithCosmosInbox(options =>
                    {
                        options.Database = CosmosTestClient.DatabaseName;
                        options.Container = InboxContainerName;
                    }),
                    typeof(WhenDeduplicatingStandaloneInbox))
                .AddMessageBrokers(receiverAssemblies: typeof(WhenDeduplicatingStandaloneInbox).Assembly);
            services.AddSingleton(testClient.Client);
            services.AddSingleton<IMessagingInfrastructure>(new CapturingInfrastructure());

            await using ServiceProvider provider = services.BuildServiceProvider();

            provider.GetServices<IHostedService>().Should().BeEmpty(
                "WithCosmosInbox registers NO relay host, NO lease processor, and NO outbox poller — it is lease-less and relay-less");

            using IServiceScope scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IBrokeredMessageInbox>().Should().BeOfType<CosmosBrokeredMessageInbox>(
                "WithCosmosInbox replaces the default inbox with the standalone Cosmos inbox");
            scope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>().Should().NotBeOfType<CosmosBrokeredMessageOutbox>(
                "the standalone inbox registers no Cosmos outbox — the outbox stays the framework default");
        }

        // CHARACTERIZATION (#253, ADR-0009 D1 amendment): an ABANDONED pre-completion marker is TAKEN OVER, not confirmed a
        // duplicate — closing the abandoned-marker permanent-loss defect the single-phase confirm-on-existence had. A
        // pending claim (Completed=false, NO completion write) is seeded directly to simulate a delivery hard-killed between
        // the 201 claim and handler completion; a fresh delivery of the SAME identity then 409s on create, the confirm
        // read-back classifies the conflicting marker PENDING, and the inbox TAKES OVER: the handler RUNS (no loss) and the
        // marker is driven to Completed=true.
        [RequiresDockerFact]
        public async Task AbandonedPendingMarkerIsTakenOverSoTheHandlerRunsAndTheMarkerCompletes()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            await EnsureInboxContainerAsync(testClient.Client);
            Container inboxContainer = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, InboxContainerName);

            string messageId = "msg-" + Guid.NewGuid().ToString("N");

            // Seed an abandoned PENDING claim directly (Completed=false, no completion write) — the exact wire shape phase 1
            // stamps before the handler, rendered through the SAME marker + partition-key + JSON path the production inbox
            // uses, so the confirm read-back classifies it PENDING exactly as it would a real abandoned claim.
            await SeedPendingMarkerAsync(inboxContainer, messageId);
            JsonElement seeded = await ReadMarkerAsync(inboxContainer, messageId);
            InspectCompleted(seeded).Should().BeFalse(
                "the seeded marker is an abandoned PENDING claim (Completed=false), not a completed one");

            var invocations = new HandlerInvocationCounter();
            await using CosmosReliabilityHarness harness = BuildHarness(testClient.Client, invocations);

            // A delivery of the same identity 409s on create; the confirm classifies the conflicting marker PENDING and the
            // inbox TAKES OVER rather than confirming a duplicate — the handler runs and the claim is completed.
            await harness.DeliverAsync(messageId, new StandaloneInboxCommand { Payload = "takeover" }, ReceiverPath);

            invocations.Count.Should().Be(1,
                "an abandoned PENDING marker is taken over — the handler RUNS (no permanent loss), it is NOT skipped as a duplicate");

            JsonElement completed = await ReadMarkerAsync(inboxContainer, messageId);
            InspectCompleted(completed).Should().BeTrue(
                "the take-over completes the claim, driving the marker to Completed=true");
        }

        // CHARACTERIZATION (#253, ADR-0009 D1 amendment): a normally-COMPLETED marker (first delivery ran the handler then
        // completed the claim) makes a redelivery confirm a duplicate on COMPLETION and SKIP the handler. Confirm-on-
        // completion is what the skip hinges on: the first delivery must leave Completed=true, and only then does the
        // redelivery suppress the handler.
        [RequiresDockerFact]
        public async Task CompletedMarkerSuppressesTheRedeliveredHandler()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            await EnsureInboxContainerAsync(testClient.Client);
            Container inboxContainer = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, InboxContainerName);

            string messageId = "msg-" + Guid.NewGuid().ToString("N");
            var invocations = new HandlerInvocationCounter();
            await using CosmosReliabilityHarness harness = BuildHarness(testClient.Client, invocations);

            // First delivery: the write-ahead claim runs the handler once, then phase 2 completes the marker.
            await harness.DeliverAsync(messageId, new StandaloneInboxCommand { Payload = "first" }, ReceiverPath);

            invocations.Count.Should().Be(1, "the first delivery of a fresh identity runs the handler exactly once");
            JsonElement afterFirst = await ReadMarkerAsync(inboxContainer, messageId);
            InspectCompleted(afterFirst).Should().BeTrue(
                "a normally-handled first delivery drives the marker to Completed=true (confirm-on-completion)");

            // Redelivery: the create 409s, the confirm reads a COMPLETED marker, and the handler is SKIPPED as a confirmed duplicate.
            await harness.DeliverAsync(messageId, new StandaloneInboxCommand { Payload = "duplicate" }, ReceiverPath);

            invocations.Count.Should().Be(1,
                "a redelivery against a COMPLETED marker confirms a duplicate on completion and SKIPS the handler");
        }

        // CHARACTERIZATION (#253, ADR-0009 D1 THIRD amendment — MONOTONIC MARKER): once a marker is Completed, NOTHING moves
        // it completed->absent — not even a redelivery whose handler is wired to THROW. Removing BestEffortCompensationDelete
        // made the marker monotonic (absent -> pending -> completed, TTL purge the only removal), so a Completed marker is a
        // STABLE TERMINAL state a failing redelivery cannot revert or resurrect. The true completed->absent race — a concurrent
        // take-over whose LOSING handler failed while the WINNER had already driven the marker to Completed=true — is not
        // deterministically reproducible against the emulator, so this encodes the guarantee as an ORDERED sequence instead of a
        // timing race: because the completed marker 409s the create and the confirm read-back classifies it COMPLETED, the
        // redelivery is SUPPRESSED BEFORE the handler is reached. The poisoned handler is therefore a TRAP that never springs —
        // had ANY redelivery path (even a failing one) reached the handler, DeliverAsync would surface the throw and fail this
        // test — proving no code path touches, deletes, or reverts the completed marker.
        [RequiresDockerFact]
        public async Task PoisonedRedeliveryDoesNotResurrectACompletedMarker()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            await EnsureInboxContainerAsync(testClient.Client);
            Container inboxContainer = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, InboxContainerName);

            string messageId = "msg-" + Guid.NewGuid().ToString("N");
            var invocations = new HandlerInvocationCounter();
            await using CosmosReliabilityHarness harness = BuildHarness(testClient.Client, invocations);

            // 1) First delivery of a fresh identity: the handler runs exactly once and phase 2 drives the marker to Completed=true.
            await harness.DeliverAsync(messageId, new StandaloneInboxCommand { Payload = "first" }, ReceiverPath);

            invocations.Count.Should().Be(1, "the first delivery of a fresh identity runs the handler exactly once");
            JsonElement afterFirst = await ReadMarkerAsync(inboxContainer, messageId);
            InspectCompleted(afterFirst).Should().BeTrue(
                "a normally-handled first delivery drives the marker to Completed=true (confirm-on-completion)");

            // 2) Redelivery of the SAME identity whose handler is wired to THROW (dedup is keyed by the inbound message id, not
            // the command type, so this poisoned command exercises the completed-marker skip for the same id). The completed
            // marker 409s the create and the confirm classifies it COMPLETED, so the redelivery is SUPPRESSED BEFORE the poisoned
            // handler is reached: DeliverAsync does NOT throw (the trap never springs) and the marker is STILL Completed=true —
            // the failing redelivery neither reverted nor deleted it (no completed->absent transition).
            Func<Task> poisonedRedelivery = () => harness.DeliverAsync(messageId, new PoisonedStandaloneInboxCommand { Payload = "poison" }, ReceiverPath);

            await poisonedRedelivery.Should().NotThrowAsync(
                "the completed marker suppresses the redelivery before the poisoned handler is reached — had it run, the handler would have thrown");
            invocations.Count.Should().Be(1,
                "the poisoned redelivery is suppressed by the completed marker — no handler (normal or poisoned) is re-invoked");

            JsonElement afterPoison = await ReadMarkerAsync(inboxContainer, messageId);
            InspectCompleted(afterPoison).Should().BeTrue(
                "a failing redelivery cannot revert or delete a completed marker — it stays Completed=true (no completed->absent)");

            // 3) A third, NORMAL redelivery is likewise SKIPPED: the completed marker still dedups and the handler is not invoked.
            await harness.DeliverAsync(messageId, new StandaloneInboxCommand { Payload = "third" }, ReceiverPath);

            invocations.Count.Should().Be(1,
                "the completed marker still dedups a subsequent normal redelivery — the handler is not re-invoked");
            JsonElement afterThird = await ReadMarkerAsync(inboxContainer, messageId);
            InspectCompleted(afterThird).Should().BeTrue(
                "the completed marker remains the stable terminal state across repeated redeliveries");
        }

        // Provisions the /idempotencyKey marker container on the shared suite database, create-if-not-exists so re-runs and
        // concurrent suite runs are idempotent (markers are keyed by a per-test-unique message id, so they never collide).
        private static async Task EnsureInboxContainerAsync(CosmosClient client)
        {
            Database database = client.GetDatabase(CosmosTestClient.DatabaseName);
            await database.CreateContainerIfNotExistsAsync(new ContainerProperties(InboxContainerName, InboxPartitionKeyPath));
        }

        // Boots the real receive pipeline with WithCosmosInbox as the only reliability registration and the counting
        // handler resolved from the auto-scanned test assembly (the shared counter is a singleton the handler reads).
        private static CosmosReliabilityHarness BuildHarness(CosmosClient client, HandlerInvocationCounter invocations)
            => CosmosReliabilityHarness.Build(
                client,
                pipeline => pipeline.WithCosmosInbox(options =>
                {
                    options.Database = CosmosTestClient.DatabaseName;
                    options.Container = InboxContainerName;
                }),
                services => services.AddSingleton(invocations));

        // Seeds an abandoned PENDING claim directly on the inbox container: the exact wire shape phase 1 stamps before the
        // handler (Completed=false, no completion write), rendered through the SAME CosmosInboxMarker + partition-key + JSON
        // path the production inbox uses, so the confirm read-back classifies it PENDING exactly as it would a real
        // hard-killed-before-completion claim.
        private static async Task SeedPendingMarkerAsync(Container container, string messageId)
        {
            CosmosInboxMarker pending = CosmosInboxMarker.From(messageId, ttlSeconds: null, completed: false);
            var partitionKey = new PartitionKey(messageId);
            IReadOnlyList<JsonElement> partitionKeyValues =
                CosmosPartitionKeyStamping.RecoverPartitionKeyValues(partitionKey, new[] { InboxPartitionKeyPath });
            JsonObject document = pending.ToJsonObject(new[] { InboxPartitionKeyPath }, partitionKeyValues);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, ChatterJson.Options);

            using var payload = new MemoryStream(bytes, writable: false);
            using ResponseMessage seeded = await container.CreateItemStreamAsync(payload, partitionKey);
            seeded.EnsureSuccessStatusCode();
        }

        // Reads the marker for messageId at the edge of the test and returns a DETACHED copy of its JSON (RootElement.Clone)
        // so the caller can inspect the completion state after the ResponseMessage/stream is disposed.
        private static async Task<JsonElement> ReadMarkerAsync(Container container, string messageId)
        {
            using ResponseMessage read = await container.ReadItemStreamAsync(CosmosItemId.ForInbox(messageId), new PartitionKey(messageId));
            read.IsSuccessStatusCode.Should().BeTrue(
                "the inbox marker for the message id must be present to inspect its completion state");
            using JsonDocument document = JsonDocument.Parse(read.Content);
            return document.RootElement.Clone();
        }

        // The marker's two-phase completion state: true only when the Completed field is boolean true (a completed claim);
        // false for a pending/abandoned claim (Completed=false or absent) — mirroring the inbox's confirm-on-completion read.
        private static bool InspectCompleted(JsonElement marker)
            => marker.TryGetProperty(CosmosInboxMarker.CompletedField, out JsonElement completed)
               && completed.ValueKind == JsonValueKind.True;
    }

    // A stateless standalone-consumer command: it carries no aggregate/outbox/partition — the standalone inbox partitions
    // its marker by the inbound message id, so the command body is inert.
    public sealed class StandaloneInboxCommand : ICommand
    {
        public string Payload { get; set; }
    }

    // The consumer handler behind the standalone inbox: it records each invocation into the shared counter so the suite
    // can assert once-only handling across a delivery and its redelivery.
    public sealed class StandaloneInboxHandler : IMessageHandler<StandaloneInboxCommand>
    {
        private readonly HandlerInvocationCounter _invocations;

        public StandaloneInboxHandler(HandlerInvocationCounter invocations)
            => _invocations = invocations ?? throw new ArgumentNullException(nameof(invocations));

        public Task Handle(StandaloneInboxCommand message, IMessageHandlerContext context)
        {
            _invocations.Increment();
            return Task.CompletedTask;
        }
    }

    // A parallel standalone-consumer command routed to a handler wired to THROW. Dedup is keyed by the inbound message id,
    // not the command type, so redelivering an already-completed id as this poisoned command exercises the completed-marker
    // skip while arming a trap: if the redelivery ever reached the handler, the throw would surface and fail the test.
    public sealed class PoisonedStandaloneInboxCommand : ICommand
    {
        public string Payload { get; set; }
    }

    // The poisoned handler THROWS on every invocation. A completed inbox marker must suppress a redelivery BEFORE the handler
    // is reached, so this handler must never be invoked; reaching it means the completed-marker dedup skip did not fire.
    public sealed class PoisonedStandaloneInboxHandler : IMessageHandler<PoisonedStandaloneInboxCommand>
    {
        public Task Handle(PoisonedStandaloneInboxCommand message, IMessageHandlerContext context)
            => throw new InvalidOperationException(
                "The poisoned standalone handler must never be invoked: a completed inbox marker must suppress the redelivery " +
                "before the handler is reached. Reaching it means the completed-marker dedup skip did not fire.");
    }

    // A thread-safe invocation tally shared (as a DI singleton) between the test and the handler it drives, so a
    // suppressed redelivery is observable as an unchanged count.
    public sealed class HandlerInvocationCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }
}
