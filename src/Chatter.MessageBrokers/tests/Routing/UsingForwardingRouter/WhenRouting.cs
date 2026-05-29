using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.UsingForwardingRouter
{
    public class WhenRouting : Testing.Core.Context
    {
        private readonly Mock<IRouteBrokeredMessages> _router = new Mock<IRouteBrokeredMessages>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();
        private readonly ForwardingRouter _sut;

        private readonly Guid _generatedId = Guid.NewGuid();

        public WhenRouting()
        {
            _idGenerator.Setup(g => g.GenerateId(It.IsAny<byte[]>())).Returns(_generatedId);
            _sut = new ForwardingRouter(_router.Object, _idGenerator.Object);
        }

        private InboundBrokeredMessage CreateInbound(byte[] body = null)
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.SetupGet(c => c.ContentType).Returns("application/json");
            return new InboundBrokeredMessage("inbound-message-id", body ?? new byte[] { 1, 2, 3 },
                new Dictionary<string, object>(), "receiver-path", converter.Object);
        }

        [Fact]
        public void MustThrowWhenRouterIsNull()
            => FluentActions.Invoking(() => new ForwardingRouter(null, _idGenerator.Object)).Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenMessageIdGeneratorIsNull()
            => FluentActions.Invoking(() => new ForwardingRouter(_router.Object, null)).Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustThrowWhenInboundIsNull()
            => await FluentActions.Invoking(async () => await _sut.Route(null, "destination", null))
                .Should().ThrowAsync<ArgumentNullException>();

        [Fact]
        public async Task MustReturnCompletedTaskWhenForwardDestinationIsWhitespace()
        {
            var result = _sut.Route(CreateInbound(), "  ", null);

            result.Should().Be(Task.CompletedTask);
            await result;
            _router.Verify(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()), Times.Never);
        }

        [Fact]
        public async Task MustRouteOutboundToForwardDestination()
        {
            OutboundBrokeredMessage routed = null;
            _router.Setup(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                   .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => routed = m)
                   .Returns(Task.CompletedTask);

            await _sut.Route(CreateInbound(), "forward-destination", null);

            routed.Destination.Should().Be("forward-destination");
        }

        [Fact]
        public async Task MustStampForwardedMessageIdFromGenerator()
        {
            OutboundBrokeredMessage routed = null;
            _router.Setup(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                   .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => routed = m)
                   .Returns(Task.CompletedTask);

            await _sut.Route(CreateInbound(), "forward-destination", null);

            routed.MessageId.Should().Be(_generatedId.ToString());
        }

        [Fact]
        public async Task MustForwardInboundBodyUnchanged()
        {
            var body = new byte[] { 9, 8, 7 };
            var inbound = CreateInbound(body);
            OutboundBrokeredMessage routed = null;
            _router.Setup(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                   .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => routed = m)
                   .Returns(Task.CompletedTask);

            await _sut.Route(inbound, "forward-destination", null);

            routed.Body.Should().BeSameAs(body);
        }

        [Fact]
        public async Task MustPassTransactionContextThroughToRouter()
        {
            var transactionContext = new TransactionContext("receiver");

            await _sut.Route(CreateInbound(), "forward-destination", transactionContext);

            _router.Verify(r => r.Route(It.IsAny<OutboundBrokeredMessage>(), transactionContext), Times.Once);
        }
    }
}
