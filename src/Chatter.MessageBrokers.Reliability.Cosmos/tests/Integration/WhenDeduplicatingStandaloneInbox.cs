using System;
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

    // A thread-safe invocation tally shared (as a DI singleton) between the test and the handler it drives, so a
    // suppressed redelivery is observable as an unchanged count.
    public sealed class HandlerInvocationCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }
}
