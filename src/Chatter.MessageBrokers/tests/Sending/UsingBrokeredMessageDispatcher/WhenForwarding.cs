using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingBrokeredMessageDispatcher
{
    public class WhenForwarding : Testing.Core.Context
    {
        private readonly Mock<IRouteBrokeredMessages> _messageRouter = new Mock<IRouteBrokeredMessages>();
        private readonly Mock<IForwardMessages> _forwarder = new Mock<IForwardMessages>();
        private readonly Mock<IBrokeredMessageAttributeDetailProvider> _detailProvider = new Mock<IBrokeredMessageAttributeDetailProvider>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();
        private readonly BrokeredMessageDispatcher _sut;

        public WhenForwarding()
            => _sut = new BrokeredMessageDispatcher(
                _messageRouter.Object,
                _forwarder.Object,
                _detailProvider.Object,
                _bodyConverterFactory.Object,
                _idGenerator.Object);

        private InboundBrokeredMessage CreateInbound()
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.SetupGet(c => c.ContentType).Returns("application/json");
            return new InboundBrokeredMessage("inbound-message-id", new byte[] { 1, 2, 3 },
                new Dictionary<string, object>(), "receiver-path", converter.Object);
        }

        [Fact]
        public async Task MustDelegateForwardToForwarderWithSameArgs()
        {
            var inbound = CreateInbound();
            var transactionContext = new TransactionContext("receiver");

            await _sut.Forward(inbound, "forward-destination", transactionContext);

            _forwarder.Verify(f => f.Route(inbound, "forward-destination", transactionContext), Times.Once);
        }

        [Fact]
        public async Task MustNotRouteThroughMessageRouterWhenForwarding()
        {
            var inbound = CreateInbound();

            await _sut.Forward(inbound, "forward-destination", new TransactionContext("receiver"));

            _messageRouter.VerifyNoOtherCalls();
        }
    }
}
