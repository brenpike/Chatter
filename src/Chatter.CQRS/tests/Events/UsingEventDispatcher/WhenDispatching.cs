using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        private static string _eventDispatchFailedLogMessage = $"Error dispatching event of type '{typeof(IMessage).Name}'.";
        private static string _eventDispatchCancelledLogMessage = $"Dispatch of event '{typeof(IMessage).Name}' was cancelled by the caller.";

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
        public async Task MustLogErrorWithTheCaughtExceptionAttached()
        {
            var handlerException = new InvalidOperationException("event handler failed");
            _handler.Setup(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>())).ThrowsAsync(handlerException);
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<InvalidOperationException>();
            _logger.VerifyWasCalled(LogLevel.Error,
                   _eventDispatchFailedLogMessage,
                   handlerException,
                   Times.Once());
        }

        [Fact]
        public async Task MustThrowExceptionWhenMessageHandlerIsInvokedAndRaisesException()
        {
            _handler.Setup(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>())).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustNotInvokeSubsequentHandlersOnceAHandlerRaisesException()
        {
            var invokedHandler = new Mock<IMessageHandler<IMessage>>();
            var faultingHandler = new Mock<IMessageHandler<IMessage>>();
            var subsequentHandler = new Mock<IMessageHandler<IMessage>>();
            var handlerException = new InvalidOperationException("event handler failed");
            faultingHandler.Setup(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>())).ThrowsAsync(handlerException);
            var listOfRegisteredHandlers = new[] { invokedHandler.Object, faultingHandler.Object, subsequentHandler.Object }.TakeWhile(_ => true);
            _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>))).Returns(listOfRegisteredHandlers);

            var thrown = await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<InvalidOperationException>();

            invokedHandler.Verify(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Once());
            subsequentHandler.Verify(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Never());
            thrown.Which.Should().BeSameAs(handlerException);
        }

        [Fact]
        public async Task MustRethrowACallerRequestedCancellationFromTheFirstHandlerAndLogItOnceAtDebugInsteadOfError()
        {
            var cancellingHandler = new Mock<IMessageHandler<IMessage>>();
            var subsequentHandler = new Mock<IMessageHandler<IMessage>>();
            var cancellation = ArrangeHandlerThatFaultsAfterAnAwait(cancellingHandler, new OperationCanceledException("The caller cancelled the dispatch."));
            ArrangeRegisteredHandlers(cancellingHandler.Object, subsequentHandler.Object);

            var thrown = await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, ContextCancelledByTheCaller())).Should().ThrowAsync<OperationCanceledException>();

            thrown.Which.Should().BeSameAs(cancellation);
            subsequentHandler.Verify(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Never());
            _logger.VerifyWasCalled(LogLevel.Debug, _eventDispatchCancelledLogMessage, cancellation, Times.Once());
            _logger.VerifyWasCalled(LogLevel.Debug, times: Times.Once());
            _logger.VerifyWasCalled(LogLevel.Error, times: Times.Never());
        }

        [Fact]
        public async Task MustLogACallerRequestedCancellationFromALaterHandlerOnceAtDebugInsteadOfError()
        {
            var invokedHandler = new Mock<IMessageHandler<IMessage>>();
            var cancellingHandler = new Mock<IMessageHandler<IMessage>>();
            var cancellation = ArrangeHandlerThatFaultsAfterAnAwait(cancellingHandler, new OperationCanceledException("The caller cancelled the dispatch."));
            ArrangeRegisteredHandlers(invokedHandler.Object, cancellingHandler.Object);

            var thrown = await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, ContextCancelledByTheCaller())).Should().ThrowAsync<OperationCanceledException>();

            thrown.Which.Should().BeSameAs(cancellation);
            invokedHandler.Verify(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Once());
            _logger.VerifyWasCalled(LogLevel.Debug, _eventDispatchCancelledLogMessage, cancellation, Times.Once());
            _logger.VerifyWasCalled(LogLevel.Debug, times: Times.Once());
            _logger.VerifyWasCalled(LogLevel.Error, times: Times.Never());
        }

        [Fact]
        public async Task MustLogErrorNotDebugWhenTheCancellationWasNotRequestedByTheCaller()
        {
            var cancellation = ArrangeHandlerThatFaultsAfterAnAwait(_handler, new OperationCanceledException("A spontaneous timeout cancelled the handler."));

            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, new MessageHandlerContext(CancellationToken.None))).Should().ThrowAsync<OperationCanceledException>();

            _logger.VerifyWasCalled(LogLevel.Error, _eventDispatchFailedLogMessage, cancellation, Times.Once());
            _logger.VerifyWasCalled(LogLevel.Error, times: Times.Once());
            _logger.VerifyWasCalled(LogLevel.Debug, times: Times.Never());
        }

        [Fact]
        public async Task MustRenderAConstructedGenericEventTypeTheWayInterpolationRenderedIt()
        {
            var genericHandler = new Mock<IMessageHandler<GenericEvent<Payload>>>();
            _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<GenericEvent<Payload>>>)))
                .Returns(new[] { genericHandler.Object }.TakeWhile(_ => true));

            var interpolatedRendering = $"Invoked event handler for '{typeof(GenericEvent<Payload>)}'.";
            var assemblyQualifiedRendering = $"Invoked event handler for '{typeof(GenericEvent<Payload>).FullName}'.";
            // A constructed generic is the only case where the two renderings differ; without this the test proves nothing.
            assemblyQualifiedRendering.Should().NotBe(interpolatedRendering);

            await _sut.Dispatch(new GenericEvent<Payload>(), null);

            _logger.VerifyWasCalled(LogLevel.Trace, interpolatedRendering, Times.Once());
            _logger.VerifyWasCalled(LogLevel.Trace, assemblyQualifiedRendering, Times.Never());
        }

        [Fact]
        public async Task MustThrowIfExceptionIsRaisedGettingMessageHandlerFromServiceProvider()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
        }

        private void ArrangeRegisteredHandlers(params IMessageHandler<IMessage>[] handlers)
            => _serviceProvider.Setup(p => p.GetService(typeof(IEnumerable<IMessageHandler<IMessage>>))).Returns(handlers.TakeWhile(_ => true));

        private static TException ArrangeHandlerThatFaultsAfterAnAwait<TException>(Mock<IMessageHandler<IMessage>> handler, TException failure) where TException : Exception
        {
            handler.Setup(h => h.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()))
                .Returns(() => FaultAfterAnAwait(failure));
            return failure;
        }

        private static MessageHandlerContext ContextCancelledByTheCaller()
        {
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            return new MessageHandlerContext(cancellationSource.Token);
        }

        private static async Task FaultAfterAnAwait(Exception failure)
        {
            await Task.Yield();
            throw failure;
        }

        public sealed class GenericEvent<T> : IEvent
        {
        }

        public sealed class Payload
        {
        }
    }
}
