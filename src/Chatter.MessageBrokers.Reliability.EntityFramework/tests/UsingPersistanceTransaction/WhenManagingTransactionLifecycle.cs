using Chatter.MessageBrokers.Reliability;
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

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingPersistanceTransaction
{
    // INVARIANT: these tests reach PersistanceTransaction the way an application does - as an
    // IPersistanceTransaction handed back by IUnitOfWork.CurrentTransaction - so they pin the public surface.
    // The module's [InternalsVisibleTo] grant also names this assembly; see WhenCreatingPersistanceTransaction
    // for the tests that use it to reach the internal type directly.
    public class WhenManagingTransactionLifecycle : Testing.Core.Context, IAsyncDisposable
    {
        private readonly SqliteOutboxContextHarness _harness;
        private readonly SqliteOutboxContext _context;
        private readonly Mock<ILoggerFactory> _loggerFactory;
        private readonly IUnitOfWork _sut;

        public WhenManagingTransactionLifecycle()
        {
            _harness = SqliteOutboxContextHarness.Create();
            _context = _harness.CreateContext();
            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            _sut = new BrokeredMessageOutbox<SqliteOutboxContext>(_context, _loggerFactory.Object);
        }

        [Fact]
        public void MustReportEmptyTransactionIdWhenNoTransactionActive()
        {
            _sut.CurrentTransaction.TransactionId.Should().Be(Guid.Empty);
        }

        [Fact]
        public async Task MustReportUnderlyingTransactionIdWhenTransactionActive()
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();

            _sut.CurrentTransaction.TransactionId.Should().Be(dbTransaction.TransactionId);
            _sut.CurrentTransaction.TransactionId.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public async Task MustPersistWhenTransactionCommits()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            await _context.Database.BeginTransactionAsync();
            await _context.Set<OutboxMessage>().AddAsync(message);
            await _context.SaveChangesAsync();
            await _sut.CurrentTransaction.CommitAsync();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().NotBeNull();
        }

        [Fact]
        public async Task MustDiscardWhenTransactionRollsBack()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            await _context.Database.BeginTransactionAsync();
            await _context.Set<OutboxMessage>().AddAsync(message);
            await _context.SaveChangesAsync();
            await _sut.CurrentTransaction.RollbackAsync();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().BeNull();
        }

        [Fact]
        public void MustNotThrowFromDisposeWhenNoTransactionActive()
        {
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            Action act = () => transaction.Dispose();

            act.Should().NotThrow();
        }

        [Fact]
        public async Task MustNotThrowFromDisposeAsyncWhenNoTransactionActive()
        {
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            Func<Task> act = async () => await transaction.DisposeAsync();

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustNotThrowFromDisposeWhenTransactionActive()
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            Action act = () => transaction.Dispose();

            act.Should().NotThrow();
        }

        [Fact]
        public async Task MustNotThrowFromDisposeAsyncWhenTransactionActive()
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            Func<Task> act = async () => await transaction.DisposeAsync();

            await act.Should().NotThrowAsync();
        }

        // INVARIANT: a disposed handle no longer owns a transaction, so commit and rollback refuse by naming the
        // disposal. Previously both dereferenced the released transaction and surfaced a NullReferenceException.
        [Fact]
        public async Task MustRefuseCommitAfterDisposeWhenTransactionActive()
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            IPersistanceTransaction transaction = _sut.CurrentTransaction;
            transaction.Dispose();

            Func<Task> act = () => transaction.CommitAsync();

            await act.Should().ThrowAsync<ObjectDisposedException>();
        }

        [Fact]
        public async Task MustRefuseRollbackAfterDisposeAsyncWhenTransactionActive()
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            IPersistanceTransaction transaction = _sut.CurrentTransaction;
            await transaction.DisposeAsync();

            Func<Task> act = () => transaction.RollbackAsync();

            await act.Should().ThrowAsync<ObjectDisposedException>();
        }

        [Fact]
        public async Task MustReportEmptyTransactionIdAfterDisposeWhenTransactionActive()
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            transaction.Dispose();

            transaction.TransactionId.Should().Be(Guid.Empty);
        }

        [Fact]
        public async Task MustNotThrowFromASecondDisposeWhenTransactionActive()
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();
            IPersistanceTransaction transaction = _sut.CurrentTransaction;
            transaction.Dispose();

            Func<Task> act = async () => await transaction.DisposeAsync();

            await act.Should().NotThrowAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _harness.DisposeAsync();
        }
    }
}
