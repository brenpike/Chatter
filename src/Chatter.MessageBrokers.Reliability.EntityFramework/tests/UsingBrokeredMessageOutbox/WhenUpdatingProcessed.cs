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

            var persisted = (await _dbContext.Set<OutboxMessage>().ToListAsync()).Single();
            persisted.ProcessedFromOutboxAtUtc.Should().NotBeNull();
            persisted.ProcessedFromOutboxAtUtc.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }

        [Fact]
        public async Task MustStampProcessedDateForAllMessagesInEnumerable()
        {
            OutboxMessage first = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            OutboxMessage second = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.ThatHasOutboxMessage(first);
            _context.ThatHasOutboxMessage(second);

            await _sut.UpdateProcessedDate(new[] { first, second });

            var persisted = await _dbContext.Set<OutboxMessage>().ToListAsync();
            persisted.Should().OnlyContain(m => m.ProcessedFromOutboxAtUtc != null);
        }

        [Fact]
        public async Task MustExcludeMessageFromUnprocessedAfterUpdate()
        {
            var message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.ThatHasOutboxMessage(message);

            await _sut.UpdateProcessedDate((OutboxMessage)message);

            var unprocessed = await _sut.GetUnprocessedMessagesFromOutbox();
            unprocessed.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReturnZeroFromSaveOutboxOnCleanContext()
        {
            var rowCount = await _sut.SaveOutboxAsync();

            rowCount.Should().Be(0);
        }
    }
}
