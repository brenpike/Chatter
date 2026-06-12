using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Context.UsingMessageHandlerContextExtensions
{
    // INVARIANT: covers the behavioral branches of the Context/MessageHandlerContextExtensions surface (distinct from the
    // Routing/Slips extensions): the IExternalDispatcher -> IBrokeredMessageDispatcher resolution gate, the InMemory
    // dispatcher factory, the IMessageBrokerContext inbound-message projection, the transaction-context read, and ONE
    // representative Send + ONE Publish overload's no-op-vs-delegate branch (the branch logic is shared across the four
    // Send/Publish overloads, so a single representative of each proves both arms).
    public class WhenExtending : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageDispatcher> _dispatcher = new Mock<IBrokeredMessageDispatcher>();
        private readonly MessageHandlerContext _context = new MessageHandlerContext(CancellationToken.None);

        private void IncludeBrokeredDispatcher()
            => _context.Container.Include<IExternalDispatcher>(_dispatcher.Object);

        // ------------------------------------------------------------------ TryGetBrokeredMessageDispatcher

        [Fact]
        public void MustReturnTrueAndDispatcherWhenExternalDispatcherIsAlsoBrokeredMessageDispatcher()
        {
            IncludeBrokeredDispatcher();

            var found = _context.TryGetBrokeredMessageDispatcher(out var resolved);

            found.Should().BeTrue();
            resolved.Should().BeSameAs(_dispatcher.Object);
        }

        [Fact]
        public void MustReturnFalseWhenExternalDispatcherIsNotBrokeredMessageDispatcher()
        {
            _context.Container.Include<IExternalDispatcher>(new Mock<IExternalDispatcher>().Object);

            var found = _context.TryGetBrokeredMessageDispatcher(out var resolved);

            found.Should().BeFalse();
            resolved.Should().BeNull();
        }

        [Fact]
        public void MustReturnFalseWhenNoExternalDispatcherRegistered()
        {
            var found = _context.TryGetBrokeredMessageDispatcher(out var resolved);

            found.Should().BeFalse();
            resolved.Should().BeNull();
        }

        // ------------------------------------------------------------------ InMemory

        [Fact]
        public void MustReturnInMemoryDispatcher()
            => _context.InMemory().Should().BeAssignableTo<IInMemoryDispatcher>();

        // ------------------------------------------------------------------ GetInboundBrokeredMessage

        [Fact]
        public void MustReturnBrokeredMessageWhenContextIsMessageBrokerContext()
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            var brokerContext = new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", CancellationToken.None, bodyConverter.Object);

            brokerContext.GetInboundBrokeredMessage().Should().BeSameAs(brokerContext.BrokeredMessage);
        }

        [Fact]
        public void MustReturnNullInboundBrokeredMessageWhenContextIsNotMessageBrokerContext()
            => _context.GetInboundBrokeredMessage().Should().BeNull();

        // ------------------------------------------------------------------ GetTransactionContext

        [Fact]
        public void MustReadTransactionContextFromContainer()
        {
            var transactionContext = new TransactionContext("receiver-path");
            _context.Container.Include(transactionContext);

            _context.GetTransactionContext().Should().BeSameAs(transactionContext);
        }

        // ------------------------------------------------------------------ Send (one representative overload): no-op vs delegate

        [Fact]
        public async Task MustReturnCompletedTaskSendingWhenNoDispatcherPresent()
        {
            var command = new Mock<CQRS.Commands.ICommand>().Object;
            await _context.Send(command);
            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustDelegateSendToDispatcherWhenPresent()
        {
            IncludeBrokeredDispatcher();
            var command = new Mock<CQRS.Commands.ICommand>().Object;

            await _context.Send(command);

            _dispatcher.Verify(d => d.Send(command, _context, null), Times.Once);
        }

        // ------------------------------------------------------------------ Publish (one representative overload): no-op vs delegate

        [Fact]
        public async Task MustReturnCompletedTaskPublishingWhenNoDispatcherPresent()
        {
            var @event = new Mock<CQRS.Events.IEvent>().Object;
            await _context.Publish(@event);
            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustDelegatePublishToDispatcherWhenPresent()
        {
            IncludeBrokeredDispatcher();
            var @event = new Mock<CQRS.Events.IEvent>().Object;

            await _context.Publish(@event);

            _dispatcher.Verify(d => d.Publish(@event, _context, null), Times.Once);
        }
    }
}
