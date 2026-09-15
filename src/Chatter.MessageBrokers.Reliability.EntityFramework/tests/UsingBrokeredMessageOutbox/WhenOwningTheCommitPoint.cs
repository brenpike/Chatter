using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    // INVARIANT: BrokeredMessageOutbox<TContext> stages outbox rows but never commits them. The enqueue is committed
    // by UnitOfWorkBehavior's single SaveChangesAsync and the drain by the outbox's own unit of work completion, so
    // each role has exactly one commit point. These tests are the regression tripwire against reintroducing a save
    // inside the staging methods themselves.
    public class WhenOwningTheCommitPoint : Testing.Core.Context, IDisposable
    {
        private readonly CommitCountingOutboxContext _context;
        private readonly BrokeredMessageOutbox<CommitCountingOutboxContext> _sut;
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter;

        public WhenOwningTheCommitPoint()
        {
            _context = CommitCountingOutboxContext.Create();

            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

            _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            _bodyConverter.SetupGet(converter => converter.ContentType).Returns("application/json");
            _bodyConverter.Setup(converter => converter.Stringify(It.IsAny<byte[]>())).Returns("stringified-body");

            _sut = new BrokeredMessageOutbox<CommitCountingOutboxContext>(_context, loggerFactory.Object);
        }

        public void Dispose() => _context.Dispose();

        private OutboundBrokeredMessage CreateOutbound(string messageId = "msg-1")
            => new OutboundBrokeredMessage(messageId, new byte[] { 1, 2, 3 }, new Dictionary<string, object>(), "test-destination", _bodyConverter.Object);

        // Seeds through the SYNCHRONOUS save so the asynchronous counter only ever observes the subject under test.
        private OutboxMessage SeedUnprocessedMessage()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.Add(message);
            _context.SaveChanges();

            return message;
        }

        [Fact]
        public async Task MustNotSaveChangesWhenSendingToOutbox()
        {
            await _sut.SendToOutbox(new[] { CreateOutbound() }, null);

            _context.SaveChangesAsyncCallCount.Should().Be(0);
        }

        [Fact]
        public async Task MustNotSaveChangesWhenUpdatingProcessedDateForASingleMessage()
        {
            var message = SeedUnprocessedMessage();

            await _sut.UpdateProcessedDate(message);

            _context.SaveChangesAsyncCallCount.Should().Be(0);
        }

        [Fact]
        public async Task MustNotSaveChangesWhenUpdatingProcessedDateForAnEnumerable()
        {
            var message = SeedUnprocessedMessage();

            await _sut.UpdateProcessedDate(new[] { message });

            _context.SaveChangesAsyncCallCount.Should().Be(0);
        }
    }
}
