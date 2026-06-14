using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingBrokeredMessageDispatcher
{
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly Mock<IRouteBrokeredMessages> _messageRouter = new Mock<IRouteBrokeredMessages>();
        private readonly Mock<IForwardMessages> _forwarder = new Mock<IForwardMessages>();
        private readonly Mock<IBrokeredMessageAttributeDetailProvider> _detailProvider = new Mock<IBrokeredMessageAttributeDetailProvider>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();
        private readonly BrokeredMessageDispatcher _sut;

        private List<OutboundBrokeredMessage> _routedMessages;

        public WhenDispatching()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _bodyConverter.Setup(c => c.Convert(It.IsAny<object>())).Returns(new byte[] { 1, 2, 3 });
            _bodyConverterFactory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(_bodyConverter.Object);
            _idGenerator.Setup(g => g.GenerateId(It.IsAny<byte[]>())).Returns(Guid.NewGuid());

            _messageRouter.Setup(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()))
                          .Callback<IEnumerable<OutboundBrokeredMessage>, TransactionContext, string>((m, _, __) => _routedMessages = m.ToList())
                          .Returns(Task.CompletedTask);

            _sut = new BrokeredMessageDispatcher(
                _messageRouter.Object,
                _forwarder.Object,
                _detailProvider.Object,
                _bodyConverterFactory.Object,
                _idGenerator.Object);
        }

        private class FakeCommand : ICommand { }

        // A real MessageBrokerContext carries an inbound InboundBrokeredMessage whose internal MessageContextImpl
        // is what BrokeredMessageDispatcher merges per-send SendOptions over via SendOptions.Create(...).Merge(...).
        private static MessageBrokerContext HandlerContextWithInbound(Mock<IBrokeredMessageBodyConverter> bodyConverter)
            => new MessageBrokerContext(
                "inbound-message-id",
                new byte[] { 9 },
                new Dictionary<string, object>(),
                "receiver-path",
                CancellationToken.None,
                bodyConverter.Object);

        [Fact]
        public async Task MustCarryPerSendOverrideOntoOutboundWhenDispatchedThroughHandlerContext()
        {
            var handlerContext = HandlerContextWithInbound(_bodyConverter);
            var options = new SendOptions().WithSubject("first-send-subject");

            await _sut.Send(new FakeCommand(), "destination", handlerContext, options);

            _routedMessages.Single().GetMessageContextByKey<string>(MessageContext.Subject).Should().Be("first-send-subject");
        }

        [Fact]
        public async Task MustNotLeakPriorSendOverrideOntoLaterDispatchOnSameHandlerContext()
        {
            var handlerContext = HandlerContextWithInbound(_bodyConverter);

            await _sut.Send(new FakeCommand(), "destination", handlerContext, new SendOptions().WithSubject("first-send-subject"));
            await _sut.Send(new FakeCommand(), "destination", handlerContext, new SendOptions());

            _routedMessages.Single().GetMessageContextByKey<string>(MessageContext.Subject).Should().BeNull();
        }
    }
}
