using Chatter.CQRS.Commands;
using Chatter.CQRS.Pipeline;
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
        private readonly CommandDispatcher _sut;

        public WhenDispatching()
        {
            _serviceProvider.Setup(p => p.GetService(typeof(IMessageHandler<IMessage>)))
                .Returns(_handler.Object);
            _serviceProvider.Setup(p => p.GetService(typeof(ICommandBehaviorPipeline<IMessage>)))
                .Returns(_pipeline.Object);
            _sut = new CommandDispatcher(_serviceProvider.Object);
        }

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
    }
}
