using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Diagnostics;
using Chatter.CQRS.Pipeline;
using Chatter.CQRS.Tests.Diagnostics;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Commands.UsingCommandDispatcher
{
    // Joined to the diagnostics collection because MustLogTheAsynchronousFaultExactlyOnceWhenDiagnosticsAreEnabled
    // attaches a PROCESS-GLOBAL .NET ActivityListener to the Chatter source; running it beside the diagnostics
    // absence tests would let those observe this class's listener.
    [Collection(DiagnosticsCollection.Name)]
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly Mock<IMessageHandler<IMessage>> _handler = new Mock<IMessageHandler<IMessage>>();
        private readonly Mock<ICommandBehaviorPipeline<IMessage>> _pipeline = new Mock<ICommandBehaviorPipeline<IMessage>>();
        private readonly LoggerCreator<CommandDispatcher> _logger;
        private readonly CommandDispatcher _sut;

        private static string _commandBehaviorExecuteLogMessage = $"Executing command behavior pipeline for '{typeof(IMessage)}'.";
        private static string _handlerInvokedLogMessage = $"No command behavior pipeline found. Executing message handler for '{typeof(IMessage)}'.";
        private static string _dispatchFailedLogMessage = $"Error dispatching command of type '{typeof(IMessage).Name}'.";

        public WhenDispatching()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IMessageHandler<IMessage>)))
                .Returns(_handler.Object);
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>)))
                .Returns(_pipeline.Object);
            _logger = New.Common().Logger<CommandDispatcher>();
            _sut = new CommandDispatcher(_serviceProvider.Object, _logger.Creation);
        }

        [Fact]
        public void MustGetDispatchType()
            => _sut.DispatchType.Should().BeSameAs(typeof(ICommand));

        [Fact]
        public async Task MustGetMessageHandler()
        {
            await _sut.Dispatch<IMessage>(null, null);
            _serviceProvider.Verify(p => p.GetService(typeof(IMessageHandler<IMessage>)), Times.Once);
        }

        [Fact]
        public async Task MustGetCommandBehaviorPipeline()
        {
            await _sut.Dispatch<IMessage>(null, null);
            _serviceProvider.Verify(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>)), Times.Once);
        }

        [Fact]
        public async Task MustExecuteCommandBehaviorPipelineWhenExists()
        {
            await _sut.Dispatch<IMessage>(null, null);
            _pipeline.Verify(p => p.Execute(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>(), It.IsAny<IMessageHandler<IMessage>>()), Times.Once);
            _handler.Verify(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Never);
        }

        [Fact]
        public async Task MustLogTraceWhenCommandBehaviorPipelineIsExecuted()
        {
            await _sut.Dispatch<IMessage>(null, null);
            _logger.VerifyWasCalled(LogLevel.Trace, _commandBehaviorExecuteLogMessage, Times.Once());
            _logger.VerifyWasCalled(LogLevel.Trace, _handlerInvokedLogMessage, Times.Never());
        }

        [Fact]
        public async Task MustInvokeMessageHandlerWhenCommandBehaviorPipelineIsNull()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>))).Returns(null);
            await _sut.Dispatch<IMessage>(null, null);
            _pipeline.Verify(p => p.Execute(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>(), It.IsAny<IMessageHandler<IMessage>>()), Times.Never);
            _handler.Verify(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Once);
        }

        [Fact]
        public async Task MustLogTraceWhenMessageHandlerIsInvoked()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>))).Returns(null);
            await _sut.Dispatch<IMessage>(null, null);
            _logger.VerifyWasCalled(LogLevel.Trace, _handlerInvokedLogMessage, Times.Once());
            _logger.VerifyWasCalled(LogLevel.Trace, _commandBehaviorExecuteLogMessage, Times.Never());
        }

        [Fact]
        public async Task MustLogErrorWhenExceptionIsCaught()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
            _logger.VerifyWasCalled(LogLevel.Error, times: Times.Once());
        }

        [Fact]
        public async Task MustThrowWhenMessageHandlerIsInvokedAndRaisesException()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>))).Returns(null);
            _handler.Setup(p => p.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>())).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustThrowWhenCommandBehaviorPipelineIsExecutedAndRaisesException()
        {
            _pipeline.Setup(p => p.Execute(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>(), It.IsAny<IMessageHandler<IMessage>>())).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustThrowIfExceptionIsRaisedGettingMessageHandlerFromServiceProvider()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IMessageHandler<IMessage>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustThrowIfExceptionIsRaisedGettingCommandBehaviorPipelineFromServiceProvider()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
        }

        [Fact]
        public async Task MustLogErrorWithTheExceptionAttachedWhenMessageHandlerFaultsAfterAnAwait()
        {
            var failure = ArrangeMessageHandlerThatFaultsAfterAnAwait();

            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<InvalidOperationException>();

            _logger.VerifyWasCalled(LogLevel.Error, _dispatchFailedLogMessage, failure, Times.Once());
        }

        [Fact]
        public async Task MustNotWriteTheStackTraceIntoTheErrorLogMessage()
        {
            var failure = ArrangeMessageHandlerThatFaultsAfterAnAwait();

            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<InvalidOperationException>();

            failure.StackTrace.Should().NotBeNullOrEmpty();
            var loggedError = _logger.LoggedMessages.Should().ContainSingle(logged => logged.level == LogLevel.Error).Subject;
            loggedError.message.Should().Be(_dispatchFailedLogMessage);
            loggedError.message.Should().NotContain(failure.StackTrace);
        }

        [Fact]
        public async Task MustLogTheAsynchronousFaultExactlyOnceWhenDiagnosticsAreEnabled()
        {
            var failure = ArrangeMessageHandlerThatFaultsAfterAnAwait();

            using (new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                ChatterDiagnostics.IsEnabled.Should().BeTrue();

                await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<InvalidOperationException>();
            }

            _logger.VerifyWasCalled(LogLevel.Error, _dispatchFailedLogMessage, failure, Times.Once());
            _logger.LoggedMessages.Should().ContainSingle(logged => logged.level == LogLevel.Error);
        }

        private InvalidOperationException ArrangeMessageHandlerThatFaultsAfterAnAwait()
        {
            var failure = new InvalidOperationException("The message handler faulted after an await.");
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>))).Returns(null);
            _handler.Setup(h => h.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>()))
                .Returns(() => FaultAfterAnAwait(failure));
            return failure;
        }

        private static async Task FaultAfterAnAwait(Exception failure)
        {
            await Task.Yield();
            throw failure;
        }
    }
}
