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
    }
}
