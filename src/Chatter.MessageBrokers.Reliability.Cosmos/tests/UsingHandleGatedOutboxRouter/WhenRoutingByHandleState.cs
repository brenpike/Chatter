using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingHandleGatedOutboxRouter
{
    public class WhenRoutingByHandleState : Testing.Core.Context
    {
        private static OutboundBrokeredMessage Message(string messageId = "msg-1")
        {
            var context = new Dictionary<string, object> { ["custom-header"] = "value" };
            return new OutboundBrokeredMessage(messageId, new byte[] { 1, 2, 3 }, context, "destination-queue", new JsonBodyConverter());
        }

        private static IEnumerable<OutboundBrokeredMessage> Messages()
            => new[] { Message("msg-0"), Message("msg-1") };

        // Builds the decorator over mock cosmos-path and inner-default routers plus a surface whose handle is preset to
        // the supplied value (null = non-participant / no open batch; non-null = participant mid-batch).
        private static (HandleGatedOutboxRouter router, Mock<IRouteBrokeredMessages> cosmos, Mock<IRouteBrokeredMessages> inner)
            Harness(ICosmosAtomicWriteHandle currentHandle)
        {
            var cosmos = new Mock<IRouteBrokeredMessages>();
            var inner = new Mock<IRouteBrokeredMessages>();
            var surface = new DocumentTierReliabilitySurface { CurrentHandle = currentHandle };
            return (new HandleGatedOutboxRouter(cosmos.Object, inner.Object, surface), cosmos, inner);
        }

        // A real handle over a do-nothing batch mock — its mere presence on the surface marks an open participant batch.
        private static ICosmosAtomicWriteHandle ActiveHandle()
            => new CosmosAtomicWriteHandle(
                Mock.Of<Container>(),
                Mock.Of<TransactionalBatch>(),
                new PartitionKey("tenant-1"),
                Array.AsReadOnly(new[] { "/tenantId" }));

        [Fact]
        public async Task MustRouteSingleMessageToInnerDefaultWhenHandleIsNull()
        {
            var (router, cosmos, inner) = Harness(currentHandle: null);
            var message = Message();

            await router.Route(message, transactionContext: null);

            inner.Verify(r => r.Route(message, null), Times.Once);
            cosmos.Verify(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()), Times.Never);
        }

        [Fact]
        public async Task MustRouteBatchToInnerDefaultWhenHandleIsNull()
        {
            var (router, cosmos, inner) = Harness(currentHandle: null);
            var messages = Messages();

            await router.Route(messages, transactionContext: null, infrastructureType: "asb");

            inner.Verify(r => r.Route(messages, null, "asb"), Times.Once);
            cosmos.Verify(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task MustNotThrowForNonParticipantSingleDispatch()
        {
            var (router, _, _) = Harness(currentHandle: null);

            Func<Task> act = () => router.Route(Message(), transactionContext: null);

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustRouteSingleMessageToCosmosWhenHandleIsActive()
        {
            var (router, cosmos, inner) = Harness(ActiveHandle());
            var message = Message();

            await router.Route(message, transactionContext: null);

            cosmos.Verify(r => r.Route(message, null), Times.Once);
            inner.Verify(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()), Times.Never);
        }

        [Fact]
        public async Task MustRouteBatchToCosmosWhenHandleIsActive()
        {
            var (router, cosmos, inner) = Harness(ActiveHandle());
            var messages = Messages();

            await router.Route(messages, transactionContext: null, infrastructureType: "asb");

            cosmos.Verify(r => r.Route(messages, null, "asb"), Times.Once);
            inner.Verify(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void MustGuardAgainstNullCosmosOutboxRouter()
        {
            Action act = () => new HandleGatedOutboxRouter(null, Mock.Of<IRouteBrokeredMessages>(), new DocumentTierReliabilitySurface());

            act.Should().Throw<ArgumentNullException>().WithParameterName("cosmosOutboxRouter");
        }

        [Fact]
        public void MustGuardAgainstNullInnerDefaultRouter()
        {
            Action act = () => new HandleGatedOutboxRouter(Mock.Of<IRouteBrokeredMessages>(), null, new DocumentTierReliabilitySurface());

            act.Should().Throw<ArgumentNullException>().WithParameterName("innerDefaultRouter");
        }

        [Fact]
        public void MustGuardAgainstNullSurface()
        {
            Action act = () => new HandleGatedOutboxRouter(Mock.Of<IRouteBrokeredMessages>(), Mock.Of<IRouteBrokeredMessages>(), null);

            act.Should().Throw<ArgumentNullException>().WithParameterName("surface");
        }
    }
}
