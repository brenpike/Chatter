using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
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

        // ------------------------------------------------------------------ Send: dispatcher-present delegation branches

        [Fact]
        public async Task MustDelegateSendWithOptionsToDispatcherWhenPresent()
        {
            IncludeDispatcher();
            var command = new Mock<CQRS.Commands.ICommand>().Object;
            var options = new SendOptions();

            await _sut.Send(command, options);

            _dispatcher.Verify(d => d.Send(command, _sut, options), Times.Once);
        }

        [Fact]
        public async Task MustDelegateSendWithDestinationPathToDispatcherWhenPresent()
        {
            IncludeDispatcher();
            var command = new Mock<CQRS.Commands.ICommand>().Object;
            var options = new SendOptions();

            await _sut.Send(command, "destination-path", options);

            _dispatcher.Verify(d => d.Send(command, "destination-path", _sut, options), Times.Once);
        }

        [Fact]
        public async Task MustReturnCompletedTaskSendingWithDestinationPathWhenNoDispatcherPresent()
        {
            var command = new Mock<CQRS.Commands.ICommand>().Object;
            await _sut.Send(command, "destination-path");
            _dispatcher.Invocations.Should().BeEmpty();
        }

        // ------------------------------------------------------------------ Publish: dispatcher-present delegation branches

        [Fact]
        public async Task MustDelegatePublishWithOptionsToDispatcherWhenPresent()
        {
            IncludeDispatcher();
            var @event = new Mock<CQRS.Events.IEvent>().Object;
            var options = new PublishOptions();

            await _sut.Publish(@event, options);

            _dispatcher.Verify(d => d.Publish(@event, _sut, options), Times.Once);
        }

        [Fact]
        public async Task MustDelegatePublishWithDestinationPathToDispatcherWhenPresent()
        {
            IncludeDispatcher();
            var @event = new Mock<CQRS.Events.IEvent>().Object;
            var options = new PublishOptions();

            await _sut.Publish(@event, "destination-path", options);

            _dispatcher.Verify(d => d.Publish(@event, "destination-path", _sut, options), Times.Once);
        }

        [Fact]
        public async Task MustDelegatePublishBatchToDispatcherWhenPresent()
        {
            IncludeDispatcher();
            var events = new[] { new Mock<CQRS.Events.IEvent>().Object };
            var options = new PublishOptions();

            await _sut.Publish((IEnumerable<CQRS.Events.IEvent>)events, options);

            _dispatcher.Verify(d => d.Publish((IEnumerable<CQRS.Events.IEvent>)events, _sut, options), Times.Once);
        }

        [Fact]
        public async Task MustReturnCompletedTaskPublishingWithDestinationPathWhenNoDispatcherPresent()
        {
            var @event = new Mock<CQRS.Events.IEvent>().Object;
            await _sut.Publish(@event, "destination-path");
            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReturnCompletedTaskPublishingBatchWhenNoDispatcherPresent()
        {
            var events = new[] { new Mock<CQRS.Events.IEvent>().Object };
            await _sut.Publish((IEnumerable<CQRS.Events.IEvent>)events);
            _dispatcher.Invocations.Should().BeEmpty();
        }
    }
}
