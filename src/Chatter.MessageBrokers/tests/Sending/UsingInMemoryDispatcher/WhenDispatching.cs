using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingInMemoryDispatcher
{
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly Mock<IMessageHandlerContext> _context = new Mock<IMessageHandlerContext>();
        private readonly Mock<IMessageDispatcher> _messageDispatcher = new Mock<IMessageDispatcher>();
        private readonly ContextContainer _container = new ContextContainer();
        private readonly InMemoryDispatcher _sut;

        public WhenDispatching()
        {
            _context.SetupGet(c => c.Container).Returns(_container);
            _sut = new InMemoryDispatcher(_context.Object);
        }

        private class FakeMessage : IMessage { }

        [Fact]
        public async Task MustDispatchViaContainedMessageDispatcherWhenPresent()
        {
            _container.Include<IMessageDispatcher>(_messageDispatcher.Object);
            var message = new FakeMessage();

            await _sut.Dispatch(message);

            _messageDispatcher.Verify(d => d.Dispatch(message, _context.Object), Times.Once);
        }

        [Fact]
        public async Task MustReturnCompletedTaskWhenNoMessageDispatcherInContainer()
        {
            var result = _sut.Dispatch(new FakeMessage());

            result.Should().Be(Task.CompletedTask);
            await result;
            _messageDispatcher.Verify(d => d.Dispatch(It.IsAny<FakeMessage>(), It.IsAny<IMessageHandlerContext>()), Times.Never);
        }
    }
}
