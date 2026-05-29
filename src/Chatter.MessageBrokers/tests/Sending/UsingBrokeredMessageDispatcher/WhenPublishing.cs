using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingBrokeredMessageDispatcher
{
    public class WhenPublishing : Testing.Core.Context
    {
        private readonly Mock<IRouteBrokeredMessages> _messageRouter = new Mock<IRouteBrokeredMessages>();
        private readonly Mock<IForwardMessages> _forwarder = new Mock<IForwardMessages>();
        private readonly Mock<IBrokeredMessageAttributeDetailProvider> _detailProvider = new Mock<IBrokeredMessageAttributeDetailProvider>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();
        private readonly BrokeredMessageDispatcher _sut;

        private List<OutboundBrokeredMessage> _routedMessages;

        public WhenPublishing()
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

        private class FakeEvent : IEvent { }

        [Fact]
        public async Task MustRouteSingleEventToProvidedDestination()
        {
            await _sut.Publish(new FakeEvent(), "topic/path");
            _routedMessages.Single().Destination.Should().Be("topic/path");
        }

        [Fact]
        public async Task MustRouteViaMessageRouterOnce()
        {
            await _sut.Publish(new FakeEvent(), "topic/path");
            _messageRouter.Verify(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task MustRouteBatchPreservingMessageCount()
        {
            _detailProvider.Setup(p => p.GetMessageName(It.IsAny<Type>())).Returns("resolved-destination");
            var events = new[] { new FakeEvent(), new FakeEvent(), new FakeEvent() };

            await _sut.Publish((IEnumerable<FakeEvent>)events);

            _routedMessages.Should().HaveCount(3);
        }

        [Fact]
        public async Task MustConvertEachMessageInBatch()
        {
            _detailProvider.Setup(p => p.GetMessageName(It.IsAny<Type>())).Returns("resolved-destination");
            var events = new[] { new FakeEvent(), new FakeEvent() };

            await _sut.Publish((IEnumerable<FakeEvent>)events);

            _bodyConverter.Verify(c => c.Convert(It.IsAny<object>()), Times.Exactly(2));
        }

        [Fact]
        public async Task MustResolveDestinationViaDetailProviderWhenNoDestinationProvided()
        {
            _detailProvider.Setup(p => p.GetMessageName(It.IsAny<Type>())).Returns("resolved-topic");

            await _sut.Publish(new FakeEvent());

            _routedMessages.Single().Destination.Should().Be("resolved-topic");
        }
    }
}
