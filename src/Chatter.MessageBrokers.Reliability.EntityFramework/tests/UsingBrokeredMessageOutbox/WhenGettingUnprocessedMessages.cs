using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
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

        [Fact]
        public async Task MustNotGetAMessageWhoseNextAttemptInstantHasNotArrived()
        {
            SeedUnprocessedMessageDueAt(DateTime.UtcNow.AddMinutes(5));
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptions(batchSize: 10, maxDispatchAttempts: null));

            var messages = await sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustGetAMessageWhoseNextAttemptInstantHasPassed()
        {
            var message = SeedUnprocessedMessageDueAt(DateTime.UtcNow.AddMinutes(-5));

            var messages = await _sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().Contain(message);
        }

        [Fact]
        public async Task MustGetAMessageThatCarriesNoNextAttemptInstant()
        {
            var message = SeedUnprocessedMessageDueAt(null);

            var messages = await _sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().Contain(message);
        }

        [Fact]
        public async Task MustExcludeAMessageThatHasSpentTheConfiguredAttemptCeiling()
        {
            SeedUnprocessedMessageWithDispatchAttempts(3);
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptions(batchSize: 10, maxDispatchAttempts: 3));

            var messages = await sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustGetAMessageStillUnderTheConfiguredAttemptCeiling()
        {
            var message = SeedUnprocessedMessageWithDispatchAttempts(2);
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptions(batchSize: 10, maxDispatchAttempts: 3));

            var messages = await sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().Contain(message);
        }

        [Fact]
        public async Task MustApplyNoAttemptCeilingWhenNoneIsConfigured()
        {
            var message = SeedUnprocessedMessageWithDispatchAttempts(int.MaxValue);
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptions(batchSize: 10, maxDispatchAttempts: null));

            var messages = await sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().Contain(message);
        }

        // INVARIANT: the due clause is NOT options-dependent. The legacy two-argument constructor names an uncapped
        // poll, not an ungated one, so a host that constructs the outbox itself still stops re-attempting a message
        // that keeps failing. Only the ceiling is options-dependent, which MustApplyNoAttemptCeilingWhenNoneIsConfigured
        // pins from the other side.
        [Fact]
        public async Task MustDueGateWithoutReliabilityOptions()
        {
            SeedUnprocessedMessageDueAt(DateTime.UtcNow.AddMinutes(5));

            var messages = await _sut.GetUnprocessedMessagesFromOutbox();

            messages.Should().BeEmpty();
        }

        // INVARIANT: the due clause and the attempt ceiling sit BEFORE the OrderBy/Take. Gating rows the take has
        // already claimed shrinks the batch rather than filling it from behind, so as few as OutboxPollBatchSize
        // held-back messages would leave the poll returning nothing at all. A batch size of one is what makes that
        // observable: the held-back message is the oldest, so it would take the only slot.
        [Fact]
        public async Task MustSpendNoBatchSlotOnAMessageThatIsNotDue()
        {
            var sentAtUtc = DateTime.UtcNow;
            SeedUnprocessedMessage(sentAtUtc.AddMinutes(-10), nextAttemptAtUtc: sentAtUtc.AddMinutes(5), dispatchAttempts: 0);
            var due = SeedUnprocessedMessageSentAt(sentAtUtc);
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptions(batchSize: 1, maxDispatchAttempts: null));

            var messages = await sut.GetUnprocessedMessagesFromOutbox();

            messages.Select(message => message.MessageId).Should().Equal(due.MessageId);
        }

        [Fact]
        public async Task MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling()
        {
            var sentAtUtc = DateTime.UtcNow;
            SeedUnprocessedMessage(sentAtUtc.AddMinutes(-10), nextAttemptAtUtc: null, dispatchAttempts: 3);
            var underCeiling = SeedUnprocessedMessageSentAt(sentAtUtc);
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptions(batchSize: 1, maxDispatchAttempts: 3));

            var messages = await sut.GetUnprocessedMessagesFromOutbox();

            messages.Select(message => message.MessageId).Should().Equal(underCeiling.MessageId);
        }

        // INVARIANT: GetUnprocessedBatch is a lookup by batch id, not an Outbox Poll Batch. Its caller runs it once
        // per unit of work with no re-poll loop behind it, so a due gate or a ceiling here would drop a message
        // nothing would ever come back for, rather than deferring it.
        [Fact]
        public async Task MustNeitherDueGateNorCeilingTheUnprocessedBatch()
        {
            var batchId = Guid.NewGuid();
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.BatchId = batchId;
            message.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(5);
            message.DispatchAttempts = 99;
            _context.ThatHasOutboxMessage(message);
            var sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, CreateOptions(batchSize: 10, maxDispatchAttempts: 3));

            var messages = await sut.GetUnprocessedBatch(batchId);

            messages.Should().Contain(message);
        }

        // INVARIANT: both clauses translate to SQL. The InMemory provider every other fact here runs on evaluates a
        // Where in process, so it cannot tell a translated predicate from one EF would refuse; SQLite is a real
        // relational provider, and an untranslatable clause throws rather than filtering. The whole point of gating
        // in the query is that a held-back message never leaves the database.
        [Fact]
        public async Task MustTranslateTheDueGateAndTheCeilingToSqlOverARelationalProvider()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var sentAtUtc = DateTime.UtcNow;
            SeedSqliteMessage(harness, 1, sentAtUtc.AddMinutes(-30), nextAttemptAtUtc: sentAtUtc.AddMinutes(5), dispatchAttempts: 1);
            SeedSqliteMessage(harness, 2, sentAtUtc.AddMinutes(-20), nextAttemptAtUtc: null, dispatchAttempts: 3);
            var taken = SeedSqliteMessage(harness, 3, sentAtUtc.AddMinutes(-10), nextAttemptAtUtc: sentAtUtc.AddMinutes(-1), dispatchAttempts: 2);
            using var context = harness.CreateContext();
            var sut = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _logger.Object, CreateOptions(batchSize: 10, maxDispatchAttempts: 3));

            var messages = await sut.GetUnprocessedMessagesFromOutbox();

            messages.Select(message => message.MessageId).Should().Equal(taken.MessageId);
        }

        private OutboxMessage SeedSqliteMessage(SqliteOutboxContextHarness harness,
                                                int id,
                                                DateTime sentToOutboxAtUtc,
                                                DateTime? nextAttemptAtUtc,
                                                int dispatchAttempts)
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.Id = id;
            message.SentToOutboxAtUtc = sentToOutboxAtUtc;
            message.NextAttemptAtUtc = nextAttemptAtUtc;
            message.DispatchAttempts = dispatchAttempts;

            using var context = harness.CreateContext();
            context.Add(message);
            context.SaveChanges();

            return message;
        }

        private OutboxMessage SeedUnprocessedMessageSentAt(DateTime sentToOutboxAtUtc)
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.SentToOutboxAtUtc = sentToOutboxAtUtc;
            _context.ThatHasOutboxMessage(message);

            return message;
        }

        private OutboxMessage SeedUnprocessedMessageDueAt(DateTime? nextAttemptAtUtc)
            => SeedUnprocessedMessage(DateTime.UtcNow, nextAttemptAtUtc, dispatchAttempts: 0);

        private OutboxMessage SeedUnprocessedMessageWithDispatchAttempts(int dispatchAttempts)
            => SeedUnprocessedMessage(DateTime.UtcNow, nextAttemptAtUtc: null, dispatchAttempts);

        private OutboxMessage SeedUnprocessedMessage(DateTime sentToOutboxAtUtc, DateTime? nextAttemptAtUtc, int dispatchAttempts)
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.SentToOutboxAtUtc = sentToOutboxAtUtc;
            message.NextAttemptAtUtc = nextAttemptAtUtc;
            message.DispatchAttempts = dispatchAttempts;
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

        // OutboxMaxDispatchAttempts has an internal setter too, so it is seeded the same way.
        private static ReliabilityOptions CreateOptions(int batchSize, int? maxDispatchAttempts)
        {
            var options = CreateOptionsWithBatchSize(batchSize);
            typeof(ReliabilityOptions).GetProperty(nameof(ReliabilityOptions.OutboxMaxDispatchAttempts)).SetValue(options, maxDispatchAttempts);

            return options;
        }
    }
}
