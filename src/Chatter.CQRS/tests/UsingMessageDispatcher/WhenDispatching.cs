using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using FluentAssertions;
using Moq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.UsingMessageDispatcher
{
    public class WhenDispatching
    {
        private readonly MessageDispatcher _sut;
        private readonly Mock<IMessageDispatcherProvider> _messageDispatcherProvider;
        private readonly Mock<IExternalDispatcher> _externalDispatcher;
        private readonly Mock<IMessageHandlerContext> _messageHandlerContext;
        private readonly Mock<IDispatchMessages> _dispatchMessages;

        public WhenDispatching()
        {
            _messageDispatcherProvider = new Mock<IMessageDispatcherProvider>();
            _dispatchMessages = new Mock<IDispatchMessages>();
            _messageDispatcherProvider.Setup(m => m.GetDispatcher<FakeEvent>()).Returns(_dispatchMessages.Object);
            _externalDispatcher = new Mock<IExternalDispatcher>();
            _messageHandlerContext = new Mock<IMessageHandlerContext>();
            _messageHandlerContext.SetupGet(c => c.Container).Returns(new ContextContainer());
            _sut = new MessageDispatcher(_messageDispatcherProvider.Object, _externalDispatcher.Object);
        }

        [Fact]
        public async Task MustAddExternalDispatcherToProvidedMessageHandlerContextContainer()
        {
            var @event = new FakeEvent();
            _dispatchMessages.Setup(d => d.Dispatch(@event, _messageHandlerContext.Object)).Returns(Task.CompletedTask);
            _messageHandlerContext.Object.Container.TryGet<IExternalDispatcher>(out var _).Should().BeFalse();
            await _sut.Dispatch(@event, _messageHandlerContext.Object);
            _messageHandlerContext.Object.Container.Get<IExternalDispatcher>().Should().BeSameAs(_externalDispatcher.Object);
            _messageHandlerContext.Object.Container.TryGet<IExternalDispatcher>(out var _).Should().BeTrue();
        }

        [Fact]
        public async Task MustAddMessageDispatcherToProvidedMessageHandlerContextContainer()
        {
            var @event = new FakeEvent();
            _dispatchMessages.Setup(d => d.Dispatch(@event, _messageHandlerContext.Object)).Returns(Task.CompletedTask);
            _messageHandlerContext.Object.Container.TryGet<IMessageDispatcher>(out var _).Should().BeFalse();
            await _sut.Dispatch(@event, _messageHandlerContext.Object);
            _messageHandlerContext.Object.Container.Get<IMessageDispatcher>().Should().BeSameAs(_sut);
            _messageHandlerContext.Object.Container.TryGet<IMessageDispatcher>(out var _).Should().BeTrue();
        }

        [Fact]
        public async Task MustDispatchWithSuppliedMessageHandlerContextIfProvided()
        {
            var @event = new FakeEvent();
            _dispatchMessages.Setup(d => d.Dispatch(@event, _messageHandlerContext.Object)).Returns(Task.CompletedTask);
            await _sut.Dispatch(@event, _messageHandlerContext.Object);
            _messageDispatcherProvider.Verify(m => m.GetDispatcher<FakeEvent>(), Times.Once());
            _dispatchMessages.Verify(d => d.Dispatch(@event, _messageHandlerContext.Object), Times.Once());
        }

        [Fact]
        public async Task MustDispatchWhenNoMessageHandlerContextIsProvided()
        {
            var @event = new FakeEvent();
            _dispatchMessages.Setup(d => d.Dispatch(@event, It.IsAny<IMessageHandlerContext>())).Returns(Task.CompletedTask);
            await _sut.Dispatch(@event);
            _messageDispatcherProvider.Verify(m => m.GetDispatcher<FakeEvent>(), Times.Once());
            _dispatchMessages.Verify(d => d.Dispatch(@event, It.IsAny<IMessageHandlerContext>()), Times.Once());
            _messageHandlerContext.Object.Container.TryGet<IMessageDispatcher>(out var _).Should().BeFalse();
            _messageHandlerContext.Object.Container.TryGet<IExternalDispatcher>(out var _).Should().BeFalse();
        }

        private class FakeEvent : IEvent { }
    }
}
