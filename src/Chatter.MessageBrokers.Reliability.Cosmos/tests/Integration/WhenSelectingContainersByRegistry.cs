using System;
using System.Collections.Generic;
using System.Linq;
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
    // #238 idiomatic-DI / per-command container registry coverage, all through Chatter public API + edge reads:
    //   - IDIOMATIC DI: the app registers ONLY a CosmosClient singleton; the provider DERIVES container handles via
    //     CosmosContainerFactory (no Container passed into the builder). Proven by an atomic commit of a participant.
    //   - MULTI-CONTAINER ATOMICITY: two command types map to two distinct containers; each is single-partition atomic
    //     (aggregate + outbox committed together in its OWN container).
    //   - NON-PARTICIPANT BYPASS: a command type with NO registration dispatches broker-direct to the capturing sink —
    //     no document-tier batch, no inbox/outbox doc (asserted via edge reads showing absence + the capture sink).
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenSelectingContainersByRegistry
    {
        private const string PrimaryReceiverPath = "primary-participant";
        private const string SecondaryReceiverPath = "secondary-participant";
        private const string NonParticipantReceiverPath = "non-participant";
        private const string OutboundDestination = "downstream-orders";
        private const string NonParticipantDestination = "non-participant-direct";
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(30);

        private readonly CosmosEmulatorFixture _emulator;

        public WhenSelectingContainersByRegistry(CosmosEmulatorFixture emulator) => _emulator = emulator;

        [RequiresDockerFact]
        public async Task IdiomaticDiDerivesContainerFromRegisteredClientAndCommitsAtomically()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container container = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);

            string partition = UniquePartition();
            string aggregateId = "agg-" + Guid.NewGuid().ToString("N");
            string messageId = "msg-" + Guid.NewGuid().ToString("N");

            // The harness registers ONLY a CosmosClient singleton; the plain overload (db/container/lease names) makes
            // the provider derive the handle via client.GetContainer — no Container is passed to the builder.
            await using CosmosReliabilityHarness harness = CosmosReliabilityHarness.Build(
                testClient.Client,
                pipeline => pipeline.WithCosmosDocumentReliability<PrimaryParticipantCommand>(
                    CosmosTestClient.DatabaseName,
                    CosmosTestClient.DocumentContainerName,
                    CosmosTestClient.LeaseContainerName,
                    TestResolvers.ResolvePartition,
                    CosmosTestClient.PartitionKeyPath),
                services => services.AddTransient<IMessageHandler<PrimaryParticipantCommand>, PrimaryParticipantHandler>());

            await harness.DeliverAsync(
                messageId,
                new PrimaryParticipantCommand { AggregateId = aggregateId, Partition = partition, Payload = "idiomatic", OutboundDestination = OutboundDestination, OutboundMessageId = NewOutboundId() },
                PrimaryReceiverPath,
                PartitionProperty(partition));

            (await CosmosEdge.WaitForPresenceAsync(container, aggregateId, partition, expectPresent: true, ReadTimeout))
                .Should().BeTrue("the derived-handle commit must persist the aggregate");
            (await CosmosEdge.WaitForCountByChatterTypeAsync(container, CosmosItemId.OutboxKind, partition, minCount: 1, ReadTimeout))
                .Should().Be(1, "the derived-handle commit must persist the co-resident outbox doc");
        }

        [RequiresDockerFact]
        public async Task MultipleCommandTypesEachCommitAtomicallyInTheirOwnContainer()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container primaryContainer = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);
            Container secondaryContainer = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.SecondDocumentContainerName);

            string partition = UniquePartition();
            string primaryAggregateId = "agg-p-" + Guid.NewGuid().ToString("N");
            string secondaryAggregateId = "agg-s-" + Guid.NewGuid().ToString("N");
            string primaryMessageId = "msg-p-" + Guid.NewGuid().ToString("N");
            string secondaryMessageId = "msg-s-" + Guid.NewGuid().ToString("N");
            // DISTINCT sentinel follow-up payloads. Each command's handler Sends OutboundFollowUp { Payload = payload },
            // which serializes into the co-resident outbox doc's MessageBody. The outbox doc id is NOT predictable
            // (SendOptions.MessageId does not survive the handler-context Send merge — see CosmosEdge remarks), so the
            // MessageBody is the command-distinguishing identity the per-container swap detection keys on: the primary
            // container's outbox must carry the primary sentinel (and NOT the secondary), proving each command's outbox
            // landed in its OWN container rather than being swapped.
            string primarySentinel = "primary-outbox-" + Guid.NewGuid().ToString("N");
            string secondarySentinel = "secondary-outbox-" + Guid.NewGuid().ToString("N");

            await using CosmosReliabilityHarness harness = CosmosReliabilityHarness.Build(
                testClient.Client,
                pipeline =>
                {
                    pipeline.WithCosmosDocumentReliability<PrimaryParticipantCommand>(
                        CosmosTestClient.DatabaseName,
                        CosmosTestClient.DocumentContainerName,
                        CosmosTestClient.LeaseContainerName,
                        TestResolvers.ResolvePartition,
                        CosmosTestClient.PartitionKeyPath);
                    pipeline.WithCosmosDocumentReliability<SecondaryParticipantCommand>(
                        CosmosTestClient.DatabaseName,
                        CosmosTestClient.SecondDocumentContainerName,
                        CosmosTestClient.LeaseContainerName,
                        TestResolvers.ResolvePartition,
                        CosmosTestClient.PartitionKeyPath);
                },
                services =>
                {
                    services.AddTransient<IMessageHandler<PrimaryParticipantCommand>, PrimaryParticipantHandler>();
                    services.AddTransient<IMessageHandler<SecondaryParticipantCommand>, SecondaryParticipantHandler>();
                });

            await harness.DeliverAsync(
                primaryMessageId,
                new PrimaryParticipantCommand { AggregateId = primaryAggregateId, Partition = partition, Payload = primarySentinel, OutboundDestination = OutboundDestination, OutboundMessageId = NewOutboundId() },
                PrimaryReceiverPath,
                PartitionProperty(partition));

            await harness.DeliverAsync(
                secondaryMessageId,
                new SecondaryParticipantCommand { AggregateId = secondaryAggregateId, Partition = partition, Payload = secondarySentinel, OutboundDestination = OutboundDestination, OutboundMessageId = NewOutboundId() },
                SecondaryReceiverPath,
                PartitionProperty(partition));

            // Each command's aggregate + outbox committed atomically in ITS OWN container.
            (await CosmosEdge.WaitForPresenceAsync(primaryContainer, primaryAggregateId, partition, expectPresent: true, ReadTimeout))
                .Should().BeTrue("the primary command commits its aggregate in the primary container");
            (await CosmosEdge.WaitForCountByChatterTypeAsync(primaryContainer, CosmosItemId.OutboxKind, partition, minCount: 1, ReadTimeout))
                .Should().Be(1, "the primary command commits its outbox doc in the primary container");
            (await CosmosEdge.WaitForPresenceAsync(secondaryContainer, secondaryAggregateId, partition, expectPresent: true, ReadTimeout))
                .Should().BeTrue("the secondary command commits its aggregate in the secondary container");
            (await CosmosEdge.WaitForCountByChatterTypeAsync(secondaryContainer, CosmosItemId.OutboxKind, partition, minCount: 1, ReadTimeout))
                .Should().Be(1, "the secondary command commits its outbox doc in the secondary container");

            // Cross-container isolation: the primary aggregate does not appear in the secondary container and vice versa.
            (await CosmosEdge.WaitForPresenceAsync(secondaryContainer, primaryAggregateId, partition, expectPresent: false, ReadTimeout))
                .Should().BeFalse("the primary aggregate is NOT written to the secondary container");
            (await CosmosEdge.WaitForPresenceAsync(primaryContainer, secondaryAggregateId, partition, expectPresent: false, ReadTimeout))
                .Should().BeFalse("the secondary aggregate is NOT written to the primary container");

            // Per-container OUTBOX IDENTITY: each container's outbox doc carries its OWN command's sentinel MessageBody.
            // The aggregate-presence checks above prove an aggregate is in the right container; this proves the
            // CO-RESIDENT OUTBOX doc is too — a swapped outbox placement (primary's outbox written to the secondary
            // container) would surface the wrong sentinel here even though the aggregate assertions pass. Keyed on the
            // command-distinguishing MessageBody because the outbox doc id is not predictable.
            IReadOnlyList<string> primaryOutboxBodies = await CosmosEdge.WaitForOutboxMessageBodiesAsync(primaryContainer, partition, minCount: 1, ReadTimeout);
            primaryOutboxBodies.Should().ContainSingle("the primary container holds exactly its own outbox doc")
                .Which.Should().Contain(primarySentinel, "the primary container's outbox carries the primary command's sentinel");
            primaryOutboxBodies.Should().NotContain(body => body.Contains(secondarySentinel),
                "the secondary command's outbox must NOT land in the primary container");

            IReadOnlyList<string> secondaryOutboxBodies = await CosmosEdge.WaitForOutboxMessageBodiesAsync(secondaryContainer, partition, minCount: 1, ReadTimeout);
            secondaryOutboxBodies.Should().ContainSingle("the secondary container holds exactly its own outbox doc")
                .Which.Should().Contain(secondarySentinel, "the secondary container's outbox carries the secondary command's sentinel");
            secondaryOutboxBodies.Should().NotContain(body => body.Contains(primarySentinel),
                "the primary command's outbox must NOT land in the secondary container");
        }

        [RequiresDockerFact]
        public async Task NonParticipantBypassesDocumentTierAndDispatchesBrokerDirect()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container container = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);

            string partition = UniquePartition();
            string messageId = "msg-" + Guid.NewGuid().ToString("N");

            // The participant is registered with a SPY resolver so the bypass is asserted against the actual contract: a
            // registry MISS must never consult the resolver. The pipeline registers a participant (so the document tier
            // IS installed) but NOT the NonParticipantCommand, proving a registry MISS bypasses the document tier rather
            // than the tier simply being absent.
            var spyResolver = new SpyPartitionResolver();
            await using CosmosReliabilityHarness harness = CosmosReliabilityHarness.Build(
                testClient.Client,
                pipeline => pipeline.WithCosmosDocumentReliability<PrimaryParticipantCommand>(
                    CosmosTestClient.DatabaseName,
                    CosmosTestClient.DocumentContainerName,
                    CosmosTestClient.LeaseContainerName,
                    spyResolver.Resolve,
                    CosmosTestClient.PartitionKeyPath),
                services =>
                {
                    services.AddTransient<IMessageHandler<PrimaryParticipantCommand>, PrimaryParticipantHandler>();
                    services.AddTransient<IMessageHandler<NonParticipantCommand>, NonParticipantHandler>();
                });

            // Deliver the non-participant WITH the partition property attached to the message. Attaching it ties the
            // absence checks below to a partition the delivered message actually carries (not an unrelated freshly
            // generated one): a tier that wrongly ran for the non-participant would resolve THIS partition and stamp its
            // inbox/outbox docs HERE, so a leak is now observable at the asserted partition.
            await harness.DeliverAsync(
                messageId,
                new NonParticipantCommand { Payload = "bypass", OutboundDestination = NonParticipantDestination, OutboundMessageId = NewOutboundId() },
                NonParticipantReceiverPath,
                PartitionProperty(partition));

            // The non-participant's Send routed broker-direct to the capturing sink (no relay involved).
            IReadOnlyList<OutboundBrokeredMessage> published = await harness.Capture.WaitForPublishedAsync(1, PublishTimeout);
            published.Where(m => m.Destination == NonParticipantDestination)
                .Should().ContainSingle("the non-participant dispatches broker-direct");

            // The bypass is closed-by-construction: a registry miss is a bare pass-through, so the resolver is NEVER
            // consulted for the non-participant delivery.
            spyResolver.CallCount.Should().Be(0, "an unregistered command bypasses the document tier and never reaches a resolver");

            // No document-tier batch was opened: no inbox marker and no outbox doc in the partition the message carries.
            (await CosmosEdge.CountByChatterTypeAsync(container, CosmosItemId.InboxKind, partition))
                .Should().Be(0, "a non-participant opens no batch — no inbox marker");
            (await CosmosEdge.CountByChatterTypeAsync(container, CosmosItemId.OutboxKind, partition))
                .Should().Be(0, "a non-participant opens no batch — no outbox doc");
        }

        private static IDictionary<string, object> PartitionProperty(string partition)
            => new Dictionary<string, object> { [TestResolvers.PartitionProperty] = partition };

        private static string NewOutboundId() => "out-" + Guid.NewGuid().ToString("N");

        private static string UniquePartition() => "pk-" + Guid.NewGuid().ToString("N");
    }
}
