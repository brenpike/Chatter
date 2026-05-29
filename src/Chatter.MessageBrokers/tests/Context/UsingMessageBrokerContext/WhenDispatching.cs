using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Context.UsingMessageBrokerContext
{
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IBrokeredMessageDispatcher> _dispatcher = new Mock<IBrokeredMessageDispatcher>();
        private readonly MessageBrokerContext _sut;

        public WhenDispatching()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _sut = new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", CancellationToken.None, _bodyConverter.Object);
        }

        private void IncludeDispatcher()
            => _sut.Container.Include<IExternalDispatcher>(_dispatcher.Object);

        [Fact]
        public async Task MustDelegateForwardToDispatcherWhenPresent()
        {
            IncludeDispatcher();
            await _sut.Forward("forward-destination");
            _dispatcher.Verify(d => d.Forward("forward-destination", _sut), Times.Once);
        }

        [Fact]
        public async Task MustReturnCompletedTaskForwardingWhenNoDispatcherPresent()
        {
            await _sut.Forward("forward-destination");
            _dispatcher.Verify(d => d.Forward(It.IsAny<string>(), It.IsAny<IMessageBrokerContext>()), Times.Never);
        }

        [Fact]
        public async Task MustReturnCompletedTaskSendingWhenNoDispatcherPresent()
        {
            var command = new Mock<CQRS.Commands.ICommand>().Object;
            await _sut.Send(command);
            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReturnCompletedTaskPublishingWhenNoDispatcherPresent()
        {
            var @event = new Mock<CQRS.Events.IEvent>().Object;
            await _sut.Publish(@event);
            _dispatcher.Invocations.Should().BeEmpty();
        }
    }
}
