using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Commands.UsingCommandDispatcher
{
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly Mock<IServiceProvider> _serviceProvider = new Mock<IServiceProvider>();
        private readonly Mock<IMessageHandler<IMessage>> _handler = new Mock<IMessageHandler<IMessage>>();
        private readonly Mock<ICommandBehaviorPipeline<IMessage>> _pipeline = new Mock<ICommandBehaviorPipeline<IMessage>>();
        private readonly LoggerCreator<CommandDispatcher> _logger;
        private readonly CommandDispatcher _sut;

        private static string _commandBehaviorExecuteLogMessage = $"Executing command behavior pipeline for '{typeof(IMessage)}'.";
        private static string _handlerInvokedLogMessage = $"No command behavior pipeline found. Executing message handler for '{typeof(IMessage)}'.";

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
            _logger.ThatLogsTrace()
                   .WithMessage(_commandBehaviorExecuteLogMessage)
                   .IsCalled(Times.Once());
            _logger.ThatLogsTrace()
                   .WithMessage(_handlerInvokedLogMessage)
                   .IsCalled(Times.Never());
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
            _logger.ThatLogsTrace()
                   .WithMessage(_handlerInvokedLogMessage)
                   .IsCalled(Times.Once());
            _logger.ThatLogsTrace()
                   .WithMessage(_commandBehaviorExecuteLogMessage)
                   .IsCalled(Times.Never());
        }

        [Fact]
        public async Task MustLogErrorWhenExceptionIsCaught()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>))).Throws<Exception>();
            await FluentActions.Invoking(async () => await _sut.Dispatch<IMessage>(null, null)).Should().ThrowAsync<Exception>();
            _logger.ThatLogsError()
                   .IsCalled(Times.Once());
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
    }
}
