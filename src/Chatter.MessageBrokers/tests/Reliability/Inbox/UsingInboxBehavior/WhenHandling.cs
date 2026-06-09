using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Inbox;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Inbox.UsingInboxBehavior
{
    public class WhenHandling : Testing.Core.Context
    {
        private class FakeCommand : ICommand { }

        private readonly Mock<IBrokeredMessageInbox> _inbox = new Mock<IBrokeredMessageInbox>();
        // FakeCommand is private, so Castle cannot proxy ILogger<InboxBehavior<FakeCommand>>;
        // a real NullLogger sidesteps the dynamic proxy. The behavior under test never asserts on logging.
        private readonly ILogger<InboxBehavior<FakeCommand>> _logger = NullLogger<InboxBehavior<FakeCommand>>.Instance;
        private readonly InboxBehavior<FakeCommand> _sut;

        public WhenHandling()
        {
            _sut = new InboxBehavior<FakeCommand>(_inbox.Object, _logger);
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenInboxIsNull()
            => FluentActions.Invoking(() => new InboxBehavior<FakeCommand>(null, _logger))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenLoggerIsNull()
            => FluentActions.Invoking(() => new InboxBehavior<FakeCommand>(_inbox.Object, null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustReceiveViaInboxWhenContextIsMessageBrokerContext()
        {
            var message = new FakeCommand();
            var brokerContext = new Mock<IMessageBrokerContext>();

            await _sut.Handle(message, brokerContext.Object, () => Task.CompletedTask);

            _inbox.Verify(i => i.ReceiveViaInbox(message, brokerContext.Object, It.IsAny<Func<Task>>()), Times.Once);
        }

        [Fact]
        public async Task MustWrapNextInTheReceiverDelegate()
        {
            var invoked = false;
            var brokerContext = new Mock<IMessageBrokerContext>();
            _inbox.Setup(i => i.ReceiveViaInbox(It.IsAny<FakeCommand>(), It.IsAny<IMessageBrokerContext>(), It.IsAny<Func<Task>>()))
                  .Returns<FakeCommand, IMessageBrokerContext, Func<Task>>((_, __, messageReceiver) => messageReceiver());

            await _sut.Handle(new FakeCommand(), brokerContext.Object, () => { invoked = true; return Task.CompletedTask; });

            invoked.Should().BeTrue();
        }

        [Fact]
        public async Task MustInvokeNextAndSkipInboxWhenContextIsNotMessageBrokerContext()
        {
            var invoked = false;
            var handlerContext = new Mock<IMessageHandlerContext>();

            await _sut.Handle(new FakeCommand(), handlerContext.Object, () => { invoked = true; return Task.CompletedTask; });

            invoked.Should().BeTrue();
            _inbox.Verify(i => i.ReceiveViaInbox(It.IsAny<FakeCommand>(), It.IsAny<IMessageBrokerContext>(), It.IsAny<Func<Task>>()), Times.Never);
        }

        [Fact]
        public async Task MustNotInvokeNextOnDuplicateMessageId()
        {
            // A real InMemoryBrokeredMessageInbox proves the behavior dedupes already-processed
            // message ids end to end rather than asserting only on a mock interaction.
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            var inbox = new InMemoryBrokeredMessageInbox(new Mock<ILogger<InMemoryBrokeredMessageInbox>>().Object);
            var sut = new InboxBehavior<FakeCommand>(inbox, _logger);

            var inbound = new InboundBrokeredMessage("id-1", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", bodyConverter.Object);
            var brokerContext = new Mock<IMessageBrokerContext>();
            brokerContext.SetupGet(c => c.BrokeredMessage).Returns(inbound);

            var firstInvoked = false;
            await sut.Handle(new FakeCommand(), brokerContext.Object, () => { firstInvoked = true; return Task.CompletedTask; });
            firstInvoked.Should().BeTrue();

            var secondInvoked = false;
            await sut.Handle(new FakeCommand(), brokerContext.Object, () => { secondInvoked = true; return Task.CompletedTask; });
            secondInvoked.Should().BeFalse();
        }
    }
}
