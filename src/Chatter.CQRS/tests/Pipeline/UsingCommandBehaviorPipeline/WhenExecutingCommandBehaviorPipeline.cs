using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Pipeline.UsingCommandBehaviorPipeline
{
    public class WhenExecutingCommandBehaviorPipeline : Testing.Core.Context
    {
        private readonly Mock<IEnumerable<ICommandBehavior<ICommand>>> _behaviors = new Mock<IEnumerable<ICommandBehavior<ICommand>>>();
        private readonly CommandBehaviorPipeline<ICommand> _sut;
        private readonly TestLogger _logger;
        private readonly CommandBehaviorOne _behaviorOne;
        private readonly CommandBehaviorTwo _behaviorTwo;
        private readonly TestMessageHandler _handler;

        public WhenExecutingCommandBehaviorPipeline()
        {
            _logger = new TestLogger();
            _behaviorOne = new CommandBehaviorOne(_logger);
            _behaviorTwo = new CommandBehaviorTwo(_logger);
            _handler = new TestMessageHandler(_logger);

            var registeredBehaviors = new ICommandBehavior<ICommand>[] { _behaviorOne, _behaviorTwo };
            _behaviors.Setup(b => b.GetEnumerator()).Returns(registeredBehaviors.ToList().GetEnumerator());
            _sut = new CommandBehaviorPipeline<ICommand>(_behaviors.Object);
        }

        [Fact]
        public async Task MustEnumerateCommandBehaviorsOnce()
        {
            await _sut.Execute(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), _handler);
            _behaviors.Verify(b => b.GetEnumerator(), Times.Once);
        }

        [Fact]
        public async Task MustWrapHandlerInRegisteredCommandBehaviors()
        {
            await _sut.Execute(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), _handler);
            _logger.Log.Should().HaveCount(5);
            _logger.Log.Should().BeEquivalentTo("behavior one before",
                                                "behavior two before",
                                                "handler",
                                                "behavior two after",
                                                "behavior one after");
        }

        [Fact]
        public async Task MustExecuteFirstRegisteredBehaviorAsTheOutermostBehavior()
        {
            await _sut.Execute(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), _handler);
            _logger.Log.Should().HaveCount(5);
            _logger.Log.Should().HaveElementAt(0, "behavior one before");
            _logger.Log.Should().HaveElementAt(4, "behavior one after");
        }

        [Fact]
        public async Task MustExecuteLastRegisteredBehaviorAsTheInnermostBehavior()
        {
            await _sut.Execute(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), _handler);
            _logger.Log.Should().HaveCount(5);
            _logger.Log.Should().HaveElementAt(1, "behavior two before");
            _logger.Log.Should().HaveElementAt(3, "behavior two after");
        }

        [Fact]
        public async Task HandlerMustBeCalledBetweenLastRegisteredBehavior()
        {
            await _sut.Execute(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), _handler);
            _logger.Log.Should().HaveCount(5);
            _logger.Log.Should().HaveElementPreceding("handler", "behavior two before");
            _logger.Log.Should().HaveElementSucceeding("handler", "behavior two after");
        }

        [Fact]
        public async Task MustCallHandlerOnlyIfNoCommandBehaviorsExist()
        {
            _behaviors.Setup(b => b.GetEnumerator()).Returns(new List<ICommandBehavior<ICommand>>().GetEnumerator());
            await _sut.Execute(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), _handler);
            _logger.Log.Should().HaveCount(1);
            _logger.Log.Should().Equal("handler");
        }

        [Fact]
        public async Task MustThrowExceptionIfHandlerIsNull()
        {
            IMessageHandler<IMessage> handler = null;
            await FluentActions.Invoking(() => _sut.Execute(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), handler)).Should().ThrowAsync<Exception>();
        }

        public class TestLogger
        {
            public List<string> Log { get; } = new List<string>();
        }

        public class CommandBehaviorOne : ICommandBehavior<ICommand>
        {
            private readonly TestLogger _logger;

            public CommandBehaviorOne(TestLogger logger) => _logger = logger;

            public async Task Handle(ICommand message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
            {
                _logger.Log.Add($"behavior one before");
                await next();
                _logger.Log.Add($"behavior one after");
            }
        }

        public class CommandBehaviorTwo : ICommandBehavior<ICommand>
        {
            private readonly TestLogger _logger;

            public CommandBehaviorTwo(TestLogger logger) => _logger = logger;

            public async Task Handle(ICommand message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
            {
                _logger.Log.Add($"behavior two before");
                await next();
                _logger.Log.Add($"behavior two after");
            }
        }

        public class TestMessageHandler : IMessageHandler<IMessage>
        {
            private readonly TestLogger _logger;

            public TestMessageHandler(TestLogger logger) => _logger = logger;

            public Task Handle(IMessage message, IMessageHandlerContext context)
            {
                _logger.Log.Add("handler");
                return Task.CompletedTask;
            }
        }

    }
}
