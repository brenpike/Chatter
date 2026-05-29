using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    public class WhenGettingUnprocessedMessages : Testing.Core.Context
    {
        private DbContextCreator _context;
        private readonly BrokeredMessageOutbox<DbContext> _sut;
        private readonly Mock<ILoggerFactory> _logger;

        public WhenGettingUnprocessedMessages()
        {
            _context = New.MessageBrokers().DbContext();
            _logger = new Mock<ILoggerFactory>();
            _sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object);
        }

        [Fact]
        public async Task MustNotGetMessagesThatAreProcessed()
        {
            var message = New.MessageBrokers().OutboxMessage();
            _context.ThatHasOutboxMessage(message);
            var messages = await _sut.GetUnprocessedMessagesFromOutbox();
            messages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustGetMessagesThatAreNotProcessed()
        {
            var message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.ThatHasOutboxMessage(message);
            var messages = await _sut.GetUnprocessedMessagesFromOutbox();
            messages.Should().Contain(message);
        }

        [Fact]
        public async Task MustGetUnprocessedBatchMessageWithMatchingBatchId()
        {
            var batchId = Guid.NewGuid();
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.BatchId = batchId;
            _context.ThatHasOutboxMessage(message);

            var messages = await _sut.GetUnprocessedBatch(batchId);

            messages.Should().Contain(message);
        }

        [Fact]
        public async Task MustNotGetProcessedMessageFromBatchEvenWhenBatchIdMatches()
        {
            var batchId = Guid.NewGuid();
            OutboxMessage message = New.MessageBrokers().OutboxMessage();
            message.BatchId = batchId;
            _context.ThatHasOutboxMessage(message);

            var messages = await _sut.GetUnprocessedBatch(batchId);

            messages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustNotGetUnprocessedMessageFromBatchWhenBatchIdDiffers()
        {
            var batchId = Guid.NewGuid();
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.BatchId = Guid.NewGuid();
            _context.ThatHasOutboxMessage(message);

            var messages = await _sut.GetUnprocessedBatch(batchId);

            messages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReturnEmptyFromBatchWhenOutboxIsEmpty()
        {
            var messages = await _sut.GetUnprocessedBatch(Guid.NewGuid());

            messages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReturnEmptyFromUnprocessedWhenOutboxIsEmpty()
        {
            var messages = await _sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().BeEmpty();
        }
    }
}
