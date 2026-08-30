using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests
{
    // Pins the single-pass consumption contract that IRouteBrokeredMessages.Route and
    // IMessagingInfrastructureDispatcher.Dispatch document: the outbound sequence is a lazy iterator whose body
    // RE-RUNS per enumeration (destination resolution, body conversion, message-id generation, trace-context
    // injection, send-span batch counting), so a second pass silently doubles that work and mis-counts the send
    // span. For the Cosmos outbox a second pass would additionally stage a DUPLICATE create-op per message onto the
    // framework-owned batch, which Cosmos rejects as a conflicting id within one transactional batch. Both the
    // outbox and the handle-gated router are compliant today; these tests exist so a Count()-for-capacity hint or a
    // defensive materialisation cannot reintroduce a second pass unnoticed.
    public class WhenDispatchSequenceIsEnumerated : Testing.Core.Context
    {
        private static OutboundBrokeredMessage Message(string messageId)
            => new OutboundBrokeredMessage(messageId,
                                           new byte[] { 1, 2, 3 },
                                           new Dictionary<string, object> { ["custom-header"] = "value" },
                                           "destination-queue",
                                           new JsonBodyConverter());

        private static EnumerationProbeSequence<OutboundBrokeredMessage> Probe(params string[] messageIds)
            => new EnumerationProbeSequence<OutboundBrokeredMessage>(messageIds.Select(Message).ToArray(), new List<string>());

        // Mirrors the outbox harness in UsingCosmosOutbox: the real internal handle over a batch mock that captures
        // every staged CreateItemStream payload, exposed on the surface so the outbox contributes its ops there.
        private static (CosmosBrokeredMessageOutbox outbox, List<Stream> staged) OutboxHarness()
        {
            var staged = new List<Stream>();
            var batch = new Mock<TransactionalBatch>();
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Callback<Stream, TransactionalBatchItemRequestOptions>((stream, _) => staged.Add(stream))
                 .Returns(batch.Object);

            var handle = new CosmosAtomicWriteHandle(Mock.Of<Container>(), batch.Object, new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { "/tenantId" }));
            var surface = new DocumentTierReliabilitySurface { CurrentHandle = handle };
            return (new CosmosBrokeredMessageOutbox(surface), staged);
        }

        // A real handle over a do-nothing batch mock — its mere presence on the surface marks an open participant batch.
        private static ICosmosAtomicWriteHandle ActiveHandle()
            => new CosmosAtomicWriteHandle(Mock.Of<Container>(),
                                           Mock.Of<TransactionalBatch>(),
                                           new PartitionKey("tenant-1"),
                                           Array.AsReadOnly(new[] { "/tenantId" }));

        private static (HandleGatedOutboxRouter router, Mock<IRouteBrokeredMessages> cosmos, Mock<IRouteBrokeredMessages> inner)
            RouterHarness(ICosmosAtomicWriteHandle currentHandle)
        {
            var cosmos = new Mock<IRouteBrokeredMessages>();
            var inner = new Mock<IRouteBrokeredMessages>();
            var surface = new DocumentTierReliabilitySurface { CurrentHandle = currentHandle };
            return (new HandleGatedOutboxRouter(cosmos.Object, inner.Object, surface), cosmos, inner);
        }

        [Fact]
        public async Task MustAskForExactlyOneEnumeratorPerSendToOutbox()
        {
            var (outbox, _) = OutboxHarness();
            var probe = Probe("msg-0", "msg-1", "msg-2");

            await outbox.SendToOutbox(probe, transactionContext: null);

            probe.EnumeratorRequestCount.Should().Be(1);
        }

        [Fact]
        public async Task MustYieldEachOutboundMessageExactlyOnceAcrossThePass()
        {
            var (outbox, _) = OutboxHarness();
            var probe = Probe("msg-0", "msg-1", "msg-2");

            await outbox.SendToOutbox(probe, transactionContext: null);

            probe.YieldCountsPerPass.Should().Equal(new[] { 3 });
            probe.YieldedCount.Should().Be(probe.ItemCount);
        }

        [Fact]
        public async Task MustStageExactlyOneOutboxOpPerOutboundMessage()
        {
            var (outbox, staged) = OutboxHarness();
            var probe = Probe("msg-0", "msg-1", "msg-2");

            await outbox.SendToOutbox(probe, transactionContext: null);

            staged.Should().HaveCount(3);
        }

        [Fact]
        public async Task MustAskForExactlyOneEnumeratorWhenSequenceIsEmpty()
        {
            var (outbox, staged) = OutboxHarness();
            var probe = Probe();

            await outbox.SendToOutbox(probe, transactionContext: null);

            probe.EnumeratorRequestCount.Should().Be(1);
            staged.Should().BeEmpty();
        }

        [Fact]
        public async Task MustHandOnTheSameUnwalkedSequenceToTheCosmosArm()
        {
            // The gate selects an arm off the surface handle alone; it must not consume the sequence itself, or the
            // selected arm would receive a sequence whose lazy body had already run once.
            var (router, cosmos, _) = RouterHarness(ActiveHandle());
            var probe = Probe("msg-0", "msg-1");

            await router.Route(probe, transactionContext: null, infrastructureType: "asb");

            cosmos.Verify(route => route.Route(probe, null, "asb"), Times.Once);
            probe.EnumeratorRequestCount.Should().Be(0);
        }

        [Fact]
        public async Task MustHandOnTheSameUnwalkedSequenceToTheInnerDefaultArm()
        {
            var (router, _, inner) = RouterHarness(currentHandle: null);
            var probe = Probe("msg-0", "msg-1");

            await router.Route(probe, transactionContext: null, infrastructureType: "asb");

            inner.Verify(route => route.Route(probe, null, "asb"), Times.Once);
            probe.EnumeratorRequestCount.Should().Be(0);
        }
    }
}
