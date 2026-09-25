using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Configuration;
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
            var brokerContext = CreateBrokerContext();

            await _sut.Handle(message, brokerContext.Object, () => Task.CompletedTask);

            _inbox.Verify(i => i.ReceiveViaInbox(message, brokerContext.Object, It.IsAny<Func<Task>>()), Times.Once);
        }

        [Fact]
        public async Task MustWrapNextInTheReceiverDelegate()
        {
            var invoked = false;
            var brokerContext = CreateBrokerContext();
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
            // message ids end to end rather than asserting only on a mock interaction. A redelivery is a
            // SECOND context carrying the same message id, because every receiver builds a fresh
            // MessageBrokerContext per delivery.
            var sut = new InboxBehavior<FakeCommand>(CreateInMemoryInbox(), _logger);

            var firstInvoked = false;
            await sut.Handle(new FakeCommand(), CreateBrokerContext("id-1").Object, () => { firstInvoked = true; return Task.CompletedTask; });
            firstInvoked.Should().BeTrue();

            var secondInvoked = false;
            await sut.Handle(new FakeCommand(), CreateBrokerContext("id-1").Object, () => { secondInvoked = true; return Task.CompletedTask; });
            secondInvoked.Should().BeFalse();
        }

        [Fact]
        public async Task MustInvokeTheNestedHandlerWhenAGatedHandlersOwnDispatchReEntersTheBehavior()
        {
            var sut = new InboxBehavior<FakeCommand>(CreateInMemoryInbox(), _logger);
            var brokerContext = CreateBrokerContext("id-1");
            var nestedInvoked = false;

            await sut.Handle(new FakeCommand(), brokerContext.Object,
                             () => sut.Handle(new FakeCommand(), brokerContext.Object, () => { nestedInvoked = true; return Task.CompletedTask; }));

            nestedInvoked.Should().BeTrue();
        }

        [Fact]
        public async Task MustReceiveViaInboxOnlyTheMessageTheDeliveryAdmitted()
        {
            var admitted = new FakeCommand();
            var brokerContext = CreateBrokerContext();
            _inbox.Setup(i => i.ReceiveViaInbox(It.IsAny<FakeCommand>(), It.IsAny<IMessageBrokerContext>(), It.IsAny<Func<Task>>()))
                  .Returns<FakeCommand, IMessageBrokerContext, Func<Task>>((_, __, messageReceiver) => messageReceiver());

            await _sut.Handle(admitted, brokerContext.Object,
                              () => _sut.Handle(new FakeCommand(), brokerContext.Object, () => Task.CompletedTask));

            _inbox.Verify(i => i.ReceiveViaInbox(It.IsAny<FakeCommand>(), It.IsAny<IMessageBrokerContext>(), It.IsAny<Func<Task>>()), Times.Once);
            _inbox.Verify(i => i.ReceiveViaInbox(admitted, brokerContext.Object, It.IsAny<Func<Task>>()), Times.Once);
        }

        [Fact]
        public async Task MustInvokeTheHandlerOfASiblingDispatchedAfterTheAdmittedMessageCompleted()
        {
            var sut = new InboxBehavior<FakeCommand>(CreateInMemoryInbox(), _logger);
            var brokerContext = CreateBrokerContext("id-1");
            await sut.Handle(new FakeCommand(), brokerContext.Object, () => Task.CompletedTask);

            var siblingInvoked = false;
            await sut.Handle(new FakeCommand(), brokerContext.Object, () => { siblingInvoked = true; return Task.CompletedTask; });

            siblingInvoked.Should().BeTrue();
        }

        [Fact]
        public async Task MustReceiveViaInboxAgainWhenTheAdmittedMessageIsRedispatchedAfterItsHandlerFailed()
        {
            var admitted = new FakeCommand();
            var brokerContext = CreateBrokerContext();
            _inbox.Setup(i => i.ReceiveViaInbox(It.IsAny<FakeCommand>(), It.IsAny<IMessageBrokerContext>(), It.IsAny<Func<Task>>()))
                  .Returns<FakeCommand, IMessageBrokerContext, Func<Task>>((_, __, messageReceiver) => messageReceiver());

            await FluentActions.Awaiting(() => _sut.Handle(admitted, brokerContext.Object, () => throw new InvalidOperationException("handler failed")))
                .Should().ThrowAsync<InvalidOperationException>();
            await _sut.Handle(admitted, brokerContext.Object, () => Task.CompletedTask);

            _inbox.Verify(i => i.ReceiveViaInbox(admitted, brokerContext.Object, It.IsAny<Func<Task>>()), Times.Exactly(2));
        }

        private static Mock<IMessageBrokerContext> CreateBrokerContext(string messageId = "id-1")
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            var inbound = new InboundBrokeredMessage(messageId, new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", bodyConverter.Object);
            var brokerContext = new Mock<IMessageBrokerContext>();
            brokerContext.SetupGet(c => c.BrokeredMessage).Returns(inbound);
            brokerContext.SetupGet(c => c.Container).Returns(new ContextContainer());
            return brokerContext;
        }

        private static InMemoryBrokeredMessageInbox CreateInMemoryInbox()
            => new InMemoryBrokeredMessageInbox(new Mock<ILogger<InMemoryBrokeredMessageInbox>>().Object,
                                                new ReliabilityOptions
                                                {
                                                    InMemoryInboxDeduplicationWindowInMinutes = 60,
                                                    InMemoryInboxMaxEntries = 200000
                                                });
    }
}
