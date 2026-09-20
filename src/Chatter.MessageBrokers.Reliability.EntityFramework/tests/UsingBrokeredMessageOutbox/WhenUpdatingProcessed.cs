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
    public class WhenUpdatingProcessed : Testing.Core.Context
    {
        private DbContextCreator _context;
        private readonly DbContext _dbContext;
        private readonly BrokeredMessageOutbox<DbContext> _sut;
        private readonly Mock<ILoggerFactory> _loggerFactory;

        public WhenUpdatingProcessed()
        {
            _context = New.MessageBrokers().DbContext();
            _dbContext = _context;
            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            _sut = new BrokeredMessageOutbox<DbContext>(_context, _loggerFactory.Object);
        }

        [Fact]
        public async Task MustStampProcessedDateForSingleMessage()
        {
            var message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.ThatHasOutboxMessage(message);

            await _sut.UpdateProcessedDate((OutboxMessage)message);

            var tracked = _dbContext.ChangeTracker.Entries<OutboxMessage>()
                .Where(e => e.State == EntityState.Modified)
                .Select(e => e.Entity)
                .Single();
            tracked.ProcessedFromOutboxAtUtc.Should().NotBeNull();
            tracked.ProcessedFromOutboxAtUtc.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }

        [Fact]
        public async Task MustStampProcessedDateForAllMessagesInEnumerable()
        {
            OutboxMessage first = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            OutboxMessage second = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.ThatHasOutboxMessage(first);
            _context.ThatHasOutboxMessage(second);

            await _sut.UpdateProcessedDate(new[] { first, second });

            var tracked = _dbContext.ChangeTracker.Entries<OutboxMessage>()
                .Where(e => e.State == EntityState.Modified)
                .Select(e => e.Entity)
                .ToList();
            tracked.Should().HaveCount(2);
            tracked.Should().OnlyContain(m => m.ProcessedFromOutboxAtUtc != null);
        }

        // INVARIANT: the claim is staged, not committed. UpdateProcessedDate leaves the entry Modified and the
        // stored row untouched until the surrounding unit of work saves, so an AsNoTracking reload — which
        // materializes a fresh instance from the store rather than returning the mutated tracked one — still
        // reports the message as unprocessed.
        [Fact]
        public async Task MustLeaveStoredMessageUnprocessedUntilTheUnitOfWorkCommits()
        {
            var message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.ThatHasOutboxMessage(message);

            await _sut.UpdateProcessedDate((OutboxMessage)message);

            var stored = (await _dbContext.Set<OutboxMessage>().AsNoTracking().ToListAsync()).Single();
            stored.ProcessedFromOutboxAtUtc.Should().BeNull();
        }

        // NOTE: every RecordDispatchAttempt fact below runs over SQLite rather than over the InMemory-provider
        // context the rest of this class uses. The store records through ExecuteUpdateAsync, and the InMemory
        // provider refuses to translate it - observed as
        // "The LINQ expression 'DbSet<OutboxMessage>().Where(...).ExecuteUpdate(...)' could not be translated".
        // A fact written against _sut would therefore report a provider limitation rather than this store's
        // behaviour.

        // INVARIANT: recording an attempt reaches the stored row on its own, without the surrounding unit of work.
        // Unlike the processed stamp above - which is staged so it commits with the handler's work - a failed
        // dispatch has no work to commit with: the transaction that carried the claim has already rolled back, so a
        // staged attempt would be rolled back with it and the message would come back due now with nothing spent.
        [Fact]
        public async Task MustCountOneMoreDispatchAttemptOnTheStoredRow()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var nextAttemptAtUtc = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
            var message = SeedSqliteMessage(harness, id: 1, dispatchAttempts: 2);
            using var context = harness.CreateContext();
            IPollableOutboxStore sut = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _loggerFactory.Object);

            await sut.RecordDispatchAttempt(message, nextAttemptAtUtc);

            var stored = ReadStoredMessage(harness, message.MessageId);
            stored.DispatchAttempts.Should().Be(3);
            stored.NextAttemptAtUtc.Should().Be(nextAttemptAtUtc);
        }

        // INVARIANT: the write bypasses the change tracker and the ProcessedFromOutboxAtUtc concurrency token. After
        // the claim loses a race the transaction rolls back, but EF does not reset the tracker: the entity still
        // carries the processed stamp as its CURRENT value against a null ORIGINAL, so a tracked SaveChanges here
        // would re-emit the very 'still unprocessed' predicate that just failed - and would commit the claim if it
        // now matched. Recording the attempt must neither throw nor disturb that tracked state, and must leave the
        // stored row unprocessed. This pins the STORE's contract and nothing about its caller: OutboxProcessor
        // records the attempt straight on the store, so no SaveChangesAsync of its own runs behind this fact - the
        // rationale is on OutboxProcessor.RecordFailedDispatchAttempt.
        [Fact]
        public async Task MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var message = SeedSqliteMessage(harness, id: 1, dispatchAttempts: 0);
            using var context = harness.CreateContext();
            IPollableOutboxStore sut = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _loggerFactory.Object);
            var tracked = (await sut.GetUnprocessedMessagesFromOutbox()).Single();
            await sut.UpdateProcessedDate(tracked);

            await sut.RecordDispatchAttempt(tracked, DateTime.UtcNow.AddSeconds(5));

            var entry = context.Entry(tracked);
            entry.State.Should().Be(EntityState.Modified);
            entry.OriginalValues[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)].Should().BeNull();
            entry.CurrentValues[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)].Should().NotBeNull();
            var stored = ReadStoredMessage(harness, message.MessageId);
            stored.ProcessedFromOutboxAtUtc.Should().BeNull();
            stored.DispatchAttempts.Should().Be(1);
        }

        // INVARIANT: the write is keyed on Id, the outbox's own primary key, so it touches exactly the message it
        // was handed. A predicate that matched more than one row would spend an attempt - and push out a next
        // attempt instant - on messages whose dispatch never failed.
        [Fact]
        public async Task MustRecordTheAttemptOnlyOnTheMessageItWasHanded()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var failing = SeedSqliteMessage(harness, id: 1, dispatchAttempts: 0);
            var untouched = SeedSqliteMessage(harness, id: 2, dispatchAttempts: 0);
            using var context = harness.CreateContext();
            IPollableOutboxStore sut = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _loggerFactory.Object);

            await sut.RecordDispatchAttempt(failing, DateTime.UtcNow.AddSeconds(5));

            ReadStoredMessage(harness, failing.MessageId).DispatchAttempts.Should().Be(1);
            var spared = ReadStoredMessage(harness, untouched.MessageId);
            spared.DispatchAttempts.Should().Be(0);
            spared.NextAttemptAtUtc.Should().BeNull();
        }

        // This one runs on the InMemory-provider context because the refusal happens before any query is built.
        [Fact]
        public async Task MustRefuseToRecordAnAttemptForAMissingMessage()
        {
            IPollableOutboxStore sut = _sut;

            Func<Task> record = () => sut.RecordDispatchAttempt(null, DateTime.UtcNow);

            await record.Should().ThrowAsync<ArgumentNullException>();
        }

        private OutboxMessage SeedSqliteMessage(SqliteOutboxContextHarness harness, int id, int dispatchAttempts)
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.Id = id;
            message.DispatchAttempts = dispatchAttempts;

            using var context = harness.CreateContext();
            context.Add(message);
            context.SaveChanges();

            return message;
        }

        private static OutboxMessage ReadStoredMessage(SqliteOutboxContextHarness harness, string messageId)
        {
            using var context = harness.CreateContext();

            return context.Set<OutboxMessage>().AsNoTracking().Single(stored => stored.MessageId == messageId);
        }
    }
}
