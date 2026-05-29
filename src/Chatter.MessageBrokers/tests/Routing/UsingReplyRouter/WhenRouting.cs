using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.UsingReplyRouter
{
    public class WhenRouting : Testing.Core.Context
    {
        private readonly Mock<IRouteBrokeredMessages> _router = new Mock<IRouteBrokeredMessages>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();
        private readonly ReplyRouter _sut;

        public WhenRouting()
        {
            _idGenerator.Setup(g => g.GenerateId(It.IsAny<byte[]>())).Returns(Guid.NewGuid());
            _router.Setup(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                   .Returns(Task.CompletedTask);
            _sut = new ReplyRouter(_router.Object, _idGenerator.Object);
        }

        private InboundBrokeredMessage CreateInbound()
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.SetupGet(c => c.ContentType).Returns("application/json");
            return new InboundBrokeredMessage("inbound-message-id", new byte[] { 1, 2, 3 },
                new Dictionary<string, object>(), "receiver-path", converter.Object);
        }

        [Fact]
        public void MustThrowWhenRouterIsNull()
            => FluentActions.Invoking(() => new ReplyRouter(null, _idGenerator.Object)).Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenMessageIdGeneratorIsNull()
            => FluentActions.Invoking(() => new ReplyRouter(_router.Object, null)).Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustReturnCompletedTaskWhenDestinationRouterContextIsNull()
        {
            var result = _sut.Route(CreateInbound(), null, null);

            result.Should().Be(Task.CompletedTask);
            await result;
            _router.Verify(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()), Times.Never);
        }

        [Fact]
        public async Task MustRouteToReplyDestinationPath()
        {
            OutboundBrokeredMessage routed = null;
            _router.Setup(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                   .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => routed = m)
                   .Returns(Task.CompletedTask);
            var context = new ReplyToRoutingContext("reply-destination", "reply-group");

            await _sut.Route(CreateInbound(), null, context);

            routed.Destination.Should().Be("reply-destination");
        }

        [Fact]
        public async Task MustStampReplyToGroupIdOnOutboundMessageContext()
        {
            OutboundBrokeredMessage routed = null;
            _router.Setup(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                   .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => routed = m)
                   .Returns(Task.CompletedTask);
            var context = new ReplyToRoutingContext("reply-destination", "reply-group");

            await _sut.Route(CreateInbound(), null, context);

            routed.MessageContext[MessageContext.ReplyToGroupId].Should().Be("reply-group");
        }

        [Fact]
        public async Task MustPassTransactionContextThroughToRouter()
        {
            var transactionContext = new TransactionContext("receiver");
            var context = new ReplyToRoutingContext("reply-destination", "reply-group");

            await _sut.Route(CreateInbound(), transactionContext, context);

            _router.Verify(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), transactionContext), Times.Once);
        }

        [Fact]
        public async Task MustWrapRoutingFailureInReplyToRoutingException()
        {
            _router.Setup(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                   .Throws(new InvalidOperationException("boom"));
            var context = new ReplyToRoutingContext("reply-destination", "reply-group");

            await FluentActions.Invoking(async () => await _sut.Route(CreateInbound(), null, context))
                .Should().ThrowAsync<ReplyToRoutingExceptions>();
        }
    }
}
