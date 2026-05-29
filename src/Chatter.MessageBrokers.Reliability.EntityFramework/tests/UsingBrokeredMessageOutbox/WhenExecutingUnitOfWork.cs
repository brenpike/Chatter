using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    public class WhenExecutingUnitOfWork : Testing.Core.Context
    {
        private DbContextCreator _context;
        private readonly DbContext _dbContext;
        private readonly BrokeredMessageOutbox<DbContext> _outbox;
        private readonly IUnitOfWork _sut;
        private readonly Mock<ILoggerFactory> _loggerFactory;

        public WhenExecutingUnitOfWork()
        {
            _context = New.MessageBrokers().DbContext();
            _dbContext = _context;
            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            _outbox = new BrokeredMessageOutbox<DbContext>(_context, _loggerFactory.Object);
            _sut = _outbox;
        }

        [Fact]
        public void MustReportNoActiveTransactionUnderInMemory()
        {
            _sut.HasActiveTransaction.Should().BeFalse();
        }

        [Fact]
        public void MustReportEmptyTransactionIdUnderInMemory()
        {
            _sut.CurrentTransaction.TransactionId.Should().Be(Guid.Empty);
        }

        // INVARIANT: ExecuteAsync calls DbContext.Database.BeginTransactionAsync, which the
        // EF Core in-memory provider does not support. AS-IS the in-memory provider surfaces
        // TransactionIgnoredWarning as a thrown InvalidOperationException before the supplied
        // operation runs, so ExecuteAsync never completes the unit of work under in-memory.
        [Fact]
        public async Task MustThrowWhenBeginningTransactionUnderInMemory()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            Func<Task> act = () => _sut.ExecuteAsync(async ct =>
            {
                await _dbContext.Set<OutboxMessage>().AddAsync(message, ct);
            }, null);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustThrowFromTransactionBeginBeforeRunningOperationThatThrows()
        {
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => _sut.ExecuteAsync(_ => throw operationException, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().NotBeSameAs(operationException);
        }

        [Fact]
        public async Task MustThrowWithNullTransactionContextUnderInMemory()
        {
            Func<Task> act = () => _sut.ExecuteAsync(_ => Task.CompletedTask, null);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }
}
