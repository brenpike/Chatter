using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    // INVARIANT: These tests exercise the reliability adapter against a real relational provider (SQLite),
    // unlike WhenExecutingUnitOfWork.cs which documents the InMemory provider's transaction-rejecting behavior.
    public class WhenExecutingUnitOfWorkOverSqlite : Testing.Core.Context, IAsyncDisposable
    {
        private readonly SqliteOutboxContextHarness _harness;
        private readonly SqliteOutboxContext _context;
        private readonly Mock<ILoggerFactory> _loggerFactory;
        private readonly IUnitOfWork _sut;

        public WhenExecutingUnitOfWorkOverSqlite()
        {
            _harness = SqliteOutboxContextHarness.Create();
            _context = _harness.CreateContext();
            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            _sut = new BrokeredMessageOutbox<SqliteOutboxContext>(_context, _loggerFactory.Object);
        }

        [Fact]
        public async Task MustPersistOutboxMessageWhenOperationCommits()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            await _sut.ExecuteAsync(async ct =>
            {
                await _context.Set<OutboxMessage>().AddAsync(message, ct);
            }, null);

            _sut.HasActiveTransaction.Should().BeFalse();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().NotBeNull();
        }

        [Fact]
        public async Task MustDiscardOutboxMessageWhenOperationThrows()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => _sut.ExecuteAsync(async ct =>
            {
                await _context.Set<OutboxMessage>().AddAsync(message, ct);
                throw operationException;
            }, null);

            await act.Should().ThrowAsync<InvalidOperationException>();

            _sut.HasActiveTransaction.Should().BeFalse();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().BeNull();
        }

        // INVARIANT: under SQLite, BeginTransactionAsync succeeds and the default NonRetryingExecutionStrategy
        // is used, so the operation's own exception propagates unchanged (same instance) - in contrast with the
        // InMemory provider, which throws from BeginTransactionAsync before the operation runs.
        [Fact]
        public async Task MustRethrowSameOperationExceptionInstanceWhenOperationThrows()
        {
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => _sut.ExecuteAsync(_ => throw operationException, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().BeSameAs(operationException);
        }

        // INVARIANT: when a transaction is already open on the context, BeginAsync returns the existing
        // CurrentTransaction rather than starting a second one. The operation therefore observes the
        // pre-existing TransactionId - the reuse evidence is the id seen inside the operation.
        [Fact]
        public async Task MustReuseExistingTransactionWhenOneIsAlreadyOpen()
        {
            await using var existingTransaction = await _context.Database.BeginTransactionAsync();
            var existingTransactionId = existingTransaction.TransactionId;

            Guid observedTransactionId = Guid.Empty;

            await _sut.ExecuteAsync(_ =>
            {
                observedTransactionId = _sut.CurrentTransaction.TransactionId;
                return Task.CompletedTask;
            }, null);

            observedTransactionId.Should().Be(existingTransactionId);
            observedTransactionId.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public async Task MustPopulateTransactionContextContainerWhenContextProvided()
        {
            var transactionContext = new TransactionContext("receiver");

            Guid transactionIdInsideOperation = Guid.Empty;

            await _sut.ExecuteAsync(_ =>
            {
                transactionIdInsideOperation = _sut.CurrentTransaction.TransactionId;
                return Task.CompletedTask;
            }, transactionContext);

            var containedTransaction = transactionContext.Container.GetOrDefault<IPersistanceTransaction>();
            containedTransaction.Should().NotBeNull();

            var containedTransactionId = transactionContext.Container.Get<Guid>("CurrentTransactionId");
            containedTransactionId.Should().Be(transactionIdInsideOperation);
            containedTransactionId.Should().NotBe(Guid.Empty);
        }

        // INVARIANT: terminal cleanup - rolling the transaction back and disposing it - can never become the
        // reported cause of a failed unit of work. Whatever the cleanup throws is logged and dropped, and the
        // exception that actually failed the unit of work is the one the caller receives.
        [Fact]
        public async Task MustRethrowTheOperationExceptionWhenRollbackAlsoThrows()
        {
            using var context = CreateFaultingContext<RollbackFaultingRelationalTransactionFactory>();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _loggerFactory.Object);
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => unitOfWork.ExecuteAsync(_ => throw operationException, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().BeSameAs(operationException);
            unitOfWork.HasActiveTransaction.Should().BeFalse();
        }

        [Fact]
        public async Task MustRethrowTheOperationExceptionWhenDisposeAlsoThrows()
        {
            using var context = CreateFaultingContext<DisposeFaultingRelationalTransactionFactory>();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _loggerFactory.Object);
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => unitOfWork.ExecuteAsync(_ => throw operationException, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().BeSameAs(operationException);
            unitOfWork.HasActiveTransaction.Should().BeFalse();
        }

        [Fact]
        public async Task MustRethrowTheOperationExceptionWhenBothRollbackAndDisposeThrow()
        {
            using var context = CreateFaultingContext<RollbackAndDisposeFaultingRelationalTransactionFactory>();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _loggerFactory.Object);
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => unitOfWork.ExecuteAsync(_ => throw operationException, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().BeSameAs(operationException);
            unitOfWork.HasActiveTransaction.Should().BeFalse();
        }

        // INVARIANT: a commit failure is itself the cause, and the rollback that follows it is still cleanup. The
        // commit exception reaches the caller even when the rollback fails on top of it.
        [Fact]
        public async Task MustPropagateTheCommitExceptionWhenRollbackAlsoThrows()
        {
            using var context = CreateFaultingContext<CommitAndRollbackFaultingRelationalTransactionFactory>();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _loggerFactory.Object);
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            Func<Task> act = () => unitOfWork.ExecuteAsync(
                ct => context.Set<OutboxMessage>().AddAsync(message, ct).AsTask(),
                null);

            (await act.Should().ThrowAsync<TransactionFaultException>())
                .Which.Phase.Should().Be(TransactionFaultPhase.Commit);
            unitOfWork.HasActiveTransaction.Should().BeFalse();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().BeNull();
        }

        // INVARIANT: once the commit has stood, a provider whose disposal faults cannot turn that durable success
        // into a thrown exception. This goes red the moment UnitOfWork's success-path disposal is awaited directly
        // rather than through CleanUpAsync: the TransactionFaultException(Dispose) then escapes ExecuteAsync while
        // the row it reports a failure for is already committed.
        [Fact]
        public async Task MustNotSurfaceADisposeFailureAfterTheCommitSucceeds()
        {
            using var context = CreateFaultingContext<DisposeFaultingRelationalTransactionFactory>();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<SqliteOutboxContext>(context, _loggerFactory.Object);
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            Func<Task> act = () => unitOfWork.ExecuteAsync(
                ct => context.Set<OutboxMessage>().AddAsync(message, ct).AsTask(),
                null);

            await act.Should().NotThrowAsync();
            unitOfWork.HasActiveTransaction.Should().BeFalse();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().NotBeNull();
        }

        // INVARIANT: a unit of work that BEGAN its own transaction leaves the context's change tracker empty once the
        // rollback has run. The CHANGE TRACKER is the discriminating observation and the store is not: nothing commits
        // on this path, so a store assertion reads identically whether the tracker was reconciled or left describing
        // writes the rolled-back transaction never made.
        [Fact]
        public async Task MustLeaveNoTrackedChangesWhenAUnitOfWorkItBeganRollsBack()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => _sut.ExecuteAsync(async ct =>
            {
                await _context.Set<OutboxMessage>().AddAsync(message, ct);
                throw operationException;
            }, null);

            await act.Should().ThrowAsync<InvalidOperationException>();

            _context.ChangeTracker.Entries().Should().BeEmpty();
        }

        // INVARIANT: the reconciliation acts only on a transaction this unit of work began. A caller who began their
        // own transaction keeps their staged entities tracked across a failed ExecuteAsync, because this unit of work
        // neither began that transaction nor completes it. The CHANGE TRACKER is again the discriminating observation
        // and the store is not: neither the caller's transaction nor the unit of work commits here. Evidence that the
        // adopted branch ran, rather than an assumption that this shape produces it: the operation observes the
        // caller's own TransactionId and the transaction is still active after the failure - both hold only when the
        // scope's BegunHere is false, since a scope this unit of work began is rolled back and disposed in the catch.
        // This goes red the moment the reconciliation in ExecuteAsync's catch stops being gated on BegunHere - the
        // caller's Added entry is then discarded by a unit of work that did not begin it.
        [Fact]
        public async Task MustLeaveTheCallersTrackedChangesAloneWhenTheCallerBeganTheTransaction()
        {
            await using var callerTransaction = await _context.Database.BeginTransactionAsync();
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            await _context.Set<OutboxMessage>().AddAsync(message);
            var operationException = new InvalidOperationException("operation failed");

            Guid observedTransactionId = Guid.Empty;

            Func<Task> act = () => _sut.ExecuteAsync(_ =>
            {
                observedTransactionId = _sut.CurrentTransaction.TransactionId;
                throw operationException;
            }, null);

            await act.Should().ThrowAsync<InvalidOperationException>();

            observedTransactionId.Should().Be(callerTransaction.TransactionId);
            _sut.HasActiveTransaction.Should().BeTrue();

            var callersEntry = _context.ChangeTracker.Entries<OutboxMessage>().Single();
            callersEntry.Entity.Should().BeSameAs(message);
            callersEntry.State.Should().Be(EntityState.Added);
        }

        private SqliteOutboxContext CreateFaultingContext<TFactory>() where TFactory : FaultingRelationalTransactionFactory
            => _harness.CreateContext(options => options.ReplaceService<IRelationalTransactionFactory, TFactory>());

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _harness.DisposeAsync();
        }
    }
}
