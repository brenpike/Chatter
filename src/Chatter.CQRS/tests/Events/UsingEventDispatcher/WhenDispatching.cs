using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Events.UsingEventDispatcher
{
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly Mock<IMessageHandler<IMessage>> _handler = new Mock<IMessageHandler<IMessage>>();
        private readonly LoggerCreator<EventDispatcher> _logger;
        private readonly EventDispatcher _sut;

        private static string _eventHandlerInvokedLogMessage = $"Invoked event handler for '{typeof(IMessage)}'.";

        public WhenDispatching()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>)))
                .Returns(new[] { _handler.Object }.TakeWhile(_ => true));
            _logger = New.Common().Logger<EventDispatcher>();
            _sut = new EventDispatcher(_serviceProvider.Object, _logger.Creation);
        }

        [Fact]
        public void MustGetDispatchType()
            => _sut.DispatchType.Should().BeSameAs(typeof(IEvent));

        [Fact]
        public async Task MustGetMessageHandler()
        {
            await _sut.Dispatch<IMessage>(null, null);
            _serviceProvider.Verify(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>)), Times.Once);
        }

        [Fact]
        public async Task MustInvokeForAllHandlersRegisteredWithServiceProvider()
        {
            var listOfRegisteredHandlers = new[] { _handler.Object, _handler.Object, _handler.Object }.TakeWhile(_ => true);
            _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>))).Returns(listOfRegisteredHandlers);
            await _sut.Dispatch<IMessage>(null, null);
            _handler.Verify(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Exactly(3));
        }

        [Fact]
        public async Task MustLogTraceForAllHandlersInvoked()
        {
            var listOfRegisteredHandlers = new[] { _handler.Object, _handler.Object, _handler.Object }.TakeWhile(_ => true);
            _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>))).Returns(listOfRegisteredHandlers);
            await _sut.Dispatch<IMessage>(null, null);
            _logger.VerifyWasCalled(LogLevel.Trace,
                   _eventHandlerInvokedLogMessage,
                   Times.Exactly(3));
        }

        [Fact]
        public async Task MustLogErrorWhenExceptionIsCaught()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
            _logger.VerifyWasCalled(LogLevel.Error, null,
                   Times.Once());
        }

        [Fact]
        public async Task MustThrowExceptionWhenMessageHandlerIsInvokedAndRaisesException()
        {
            _handler.Setup(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>())).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustThrowIfExceptionIsRaisedGettingMessageHandlerFromServiceProvider()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
        }
    }
}
