using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
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

        [Fact]
        public async Task MustTakeOnlyTheConfiguredBatchSizeOldestFirst()
        {
            var sentAtUtc = DateTime.UtcNow;
            SeedUnprocessedMessageSentAt(sentAtUtc);
            var oldest = SeedUnprocessedMessageSentAt(sentAtUtc.AddMinutes(-10));
            var middle = SeedUnprocessedMessageSentAt(sentAtUtc.AddMinutes(-5));
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptionsWithBatchSize(2));

            var messages = await sut.GetUnprocessedMessagesFromOutbox();

            messages.Select(message => message.MessageId).Should().Equal(oldest.MessageId, middle.MessageId);
        }

        // The claim check that makes claim-before-dispatch safe under concurrency lives in the loaded ORIGINAL value
        // of the ProcessedFromOutboxAtUtc concurrency token: a tracked entity keeps its loaded null, so the claim
        // update carries 'WHERE ProcessedFromOutboxAtUtc IS NULL'. An AsNoTracking or otherwise detached entity has
        // original == current == the new stamp, matches no row, and every claim fails. Pinned end-to-end by
        // Integration/WhenClaimingOutboxConcurrentlyOnSqlServer.
        [Fact]
        public async Task MustReturnTrackedMessagesSoTheClaimKeepsTheLoadedOriginalValue()
        {
            var message = SeedUnprocessedMessageSentAt(DateTime.UtcNow);
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptionsWithBatchSize(10));
            DbContext context = _context;

            var polled = (await sut.GetUnprocessedMessagesFromOutbox()).Single();

            polled.Should().BeSameAs(message);
            context.Entry(polled).State.Should().Be(EntityState.Unchanged);

            await sut.UpdateProcessedDate(polled);

            var entry = context.Entry(polled);
            entry.State.Should().Be(EntityState.Modified);
            entry.OriginalValues[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)].Should().BeNull();
            entry.CurrentValues[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)].Should().NotBeNull();
        }

        [Fact]
        public async Task MustGetEveryUnprocessedMessageWhenNoBatchSizeIsConfigured()
        {
            var sentAtUtc = DateTime.UtcNow;
            var seededCount = 101;
            for (var seeded = 0; seeded < seededCount; seeded++)
            {
                SeedUnprocessedMessageSentAt(sentAtUtc.AddSeconds(-seeded));
            }

            var messages = await _sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().HaveCount(seededCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void MustRefuseABatchSizeBelowOne(int batchSize)
        {
            Action construct = () => new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptionsWithBatchSize(batchSize));

            construct.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void MustRefuseMissingReliabilityOptions()
        {
            Action construct = () => new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, null);

            construct.Should().Throw<ArgumentNullException>();
        }

        private OutboxMessage SeedUnprocessedMessageSentAt(DateTime sentToOutboxAtUtc)
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.SentToOutboxAtUtc = sentToOutboxAtUtc;
            _context.ThatHasOutboxMessage(message);

            return message;
        }

        // ReliabilityOptions.OutboxPollBatchSize has an internal setter and ReliabilityOptionsBuilder refuses a value
        // below 1, so a refused value can only be handed to the store by a hand-built options instance.
        private static ReliabilityOptions CreateOptionsWithBatchSize(int batchSize)
        {
            var options = new ReliabilityOptions();
            typeof(ReliabilityOptions).GetProperty(nameof(ReliabilityOptions.OutboxPollBatchSize)).SetValue(options, batchSize);

            return options;
        }
    }
}
