using Chatter.MessageBrokers.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    internal sealed class UnitOfWork<TContext> : IUnitOfWork where TContext : DbContext
    {
        private const string WhileHandlingAnEarlierFailure = "while handling an earlier failure; the earlier failure is the one reported";
        private const string AfterTheCommitStood = "after the unit of work committed; the commit stands";

        private readonly TContext _context;
        private readonly ILogger<UnitOfWork<TContext>> _logger;

        public UnitOfWork(TContext context, ILogger<UnitOfWork<TContext>> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IPersistanceTransaction CurrentTransaction
        {
            get
            {
                var ambientTransaction = _context.Database.CurrentTransaction;
                return ambientTransaction is null
                    ? (IPersistanceTransaction)NoActiveTransaction.Instance
                    : PersistanceTransaction.Create(ambientTransaction);
            }
        }

        public bool HasActiveTransaction => _context?.Database?.CurrentTransaction != null;

        public Task ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            if (strategy.RetriesOnFailure)
            {
                throw new InvalidOperationException(
                    $"UnitOfWork<{typeof(TContext).Name}> refuses to run under execution strategy '{strategy.GetType().Name}' because its RetriesOnFailure is true. " +
                    $"Re-executing a unit of work would silently discard or duplicate a handler's work: the first attempt's SaveChangesAsync accepts every tracked change, " +
                    $"so a retry after a failed commit saves nothing and commits an empty transaction, and only the application can supply the verifySucceeded predicate " +
                    $"that would make re-execution safe. Remove EnableRetryOnFailure from '{typeof(TContext).Name}', or stop registering the unit of work for it " +
                    $"(WithUnitOfWorkBehavior, WithInboxBehavior, WithOutboxProcessingBehavior). Chatter's recovery pipeline and broker redelivery already retry at their own layers.");
            }

            return strategy.ExecuteAsync(async ct =>
            {
                // INVARIANT: every terminal step on both paths runs through CleanUpAsync, so no terminal step can
                // report a failure that is not the causal failure. On the failure path the causal failure is the
                // operation's own exception; on the success path the commit has already stood by the time the
                // disposal runs, so a provider whose disposal faults must not turn a durable success into a throw.
                // Oracle: MustNotSurfaceADisposeFailureAfterTheCommitSucceeds, which goes red the moment the
                // success-path disposal below is awaited directly instead of through the guard. The transaction is
                // begun outside the try because the catch clause reads the scope; the compiler holds that placement,
                // no test does.
                var scope = await BeginAsync(ct).ConfigureAwait(false);
                try
                {
                    transactionContext?.Container.Include<IPersistanceTransaction>(scope.Transaction);
                    transactionContext?.Container.Include("CurrentTransactionId", scope.Transaction.TransactionId);

                    await operation(ct).ConfigureAwait(false);
                    await CompleteAsync(scope, ct).ConfigureAwait(false);
                    _logger.LogTrace($"Unit of work completed successfully.");
                }
                catch (Exception ex)
                {
                    await CleanUpAsync(() => RollbackAsync(scope, CancellationToken.None), "roll back", WhileHandlingAnEarlierFailure).ConfigureAwait(false);

                    // INVARIANT: a unit of work that began its own transaction reconciles the context's change tracker
                    // with the rollback. ELIMINATED CLASS: a change-tracker entry describing a write a transaction
                    // this unit of work rolled back never made. EF accepts changes at SaveChangesAsync time rather
                    // than at COMMIT time, so every entity a failed attempt flushed stays tracked in its post-flush
                    // state, and FindAsync resolves from the identity map ahead of the store - which is how a retry
                    // over the same scoped context reads a row the store does not hold (#512). The tracker is cleared
                    // wholesale rather than entry by entry: a targeted detach leaves the flushed entity's companions
                    // tracked, which is the same wrong answer read through a different object.
                    // Oracles: WhenExecutingUnitOfWorkOverSqlite.MustLeaveNoTrackedChangesWhenAUnitOfWorkItBeganRollsBack
                    // and WhenReceivingViaInbox.MustNotSuppressARedeliveryOverTheSameContextWhenTheCommitFailedAfterTheHandlerReturned.
                    // Deleting this reconciliation reddens those TWO facts and nothing else, on both target
                    // frameworks - measured by deleting it and counting, not predicted.
                    // The BegunHere gate carries the ownership rule this type states below: a unit of work that
                    // adopted a caller's transaction rolls nothing back and did not begin the state staged on it, so
                    // it leaves that state to the owner who completes the transaction. Oracle:
                    // WhenExecutingUnitOfWorkOverSqlite.MustLeaveTheCallersTrackedChangesAloneWhenTheCallerBeganTheTransaction;
                    // reconciling without the gate reddens that one fact and nothing else, on both target frameworks
                    // (measured).
                    // The SUCCESS path is untouched, because a commit that stood leaves a truthful tracker. ADR-0028's
                    // indeterminate commit is neither claimed nor denied: this runs after a rollback attempt whose
                    // outcome is unknown, and clearing the tracker asserts nothing about the store either way.
                    // Decision: docs/adr/0034-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md.
                    if (scope.BegunHere)
                    {
                        _context.ChangeTracker.Clear();
                    }

                    await CleanUpAsync(() => scope.DisposeAsync().AsTask(), "dispose", WhileHandlingAnEarlierFailure).ConfigureAwait(false);
                    _logger.LogError(ex, "Error occurred during unit of work");
                    throw;
                }

                await CleanUpAsync(() => scope.DisposeAsync().AsTask(), "dispose", AfterTheCommitStood).ConfigureAwait(false);
            }, cancellationToken);
        }

        // INVARIANT: cleanup runs under CancellationToken.None. Honouring the token that failed the unit of work
        // would make a cancelled operation skip its own rollback and leave the transaction open.
        private async Task CleanUpAsync(Func<Task> cleanUp, string cleanUpDescription, string cleanUpCircumstance)
        {
            try
            {
                await cleanUp().ConfigureAwait(false);
            }
            catch (Exception cleanUpException)
            {
                _logger.LogWarning(cleanUpException, "Failed to {CleanUpDescription} the unit of work's transaction {CleanUpCircumstance}.", cleanUpDescription, cleanUpCircumstance);
            }
        }

        private async Task CompleteAsync(UnitOfWorkTransaction scope, CancellationToken cancellationToken = default)
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Change(s) saved for context '{typeof(TContext).Name}'.");

            if (!scope.BegunHere)
            {
                _logger.LogTrace($"Transaction id '{scope.Transaction.TransactionId}' was begun by the caller. Leaving it open for its owner to commit.");
                return;
            }

            _logger.LogTrace($"Committing transaction id '{scope.Transaction.TransactionId}'.");
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Transaction committed for context '{typeof(TContext).Name}'.");
        }

        private async ValueTask<UnitOfWorkTransaction> BeginAsync(CancellationToken cancellationToken = default)
        {
            var ambientTransaction = _context.Database.CurrentTransaction;
            if (ambientTransaction != null)
            {
                _logger.LogTrace($"Cannot create new transaction as the unit of work is already part of transaction id '{ambientTransaction.TransactionId}'.");
                return new UnitOfWorkTransaction(PersistanceTransaction.Create(ambientTransaction), begunHere: false);
            }

            var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Transaction created for context '{typeof(TContext).Name}' with transaction id '{transaction.TransactionId}'");
            return new UnitOfWorkTransaction(PersistanceTransaction.Create(transaction), begunHere: true);
        }

        private async Task RollbackAsync(UnitOfWorkTransaction scope, CancellationToken cancellationToken = default)
        {
            if (!scope.BegunHere)
            {
                _logger.LogTrace($"Transaction id '{scope.Transaction.TransactionId}' was begun by the caller. Leaving it to its owner to roll back.");
                return;
            }

            _logger.LogTrace($"Rolling back transaction id '{scope.Transaction.TransactionId}'.");
            await scope.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Transaction rolled back for context '{typeof(TContext).Name}'.");
        }

        // INVARIANT: ownership is captured once, when the transaction is begun, and carried here. Commit, rollback
        // and dispose act only on a transaction this unit of work began; a transaction begun by the caller is
        // participated in - the unit of work's SaveChangesAsync still flushes into it - and left for its owner to
        // complete. Ownership is never re-derived from the context's ambient transaction.
        private sealed class UnitOfWorkTransaction : IAsyncDisposable
        {
            public UnitOfWorkTransaction(IPersistanceTransaction transaction, bool begunHere)
            {
                Transaction = transaction;
                BegunHere = begunHere;
            }

            public IPersistanceTransaction Transaction { get; }

            public bool BegunHere { get; }

            public Task CommitAsync(CancellationToken cancellationToken)
                => BegunHere ? Transaction.CommitAsync(cancellationToken) : Task.CompletedTask;

            public Task RollbackAsync(CancellationToken cancellationToken)
                => BegunHere ? Transaction.RollbackAsync(cancellationToken) : Task.CompletedTask;

            public ValueTask DisposeAsync()
                => BegunHere ? Transaction.DisposeAsync() : default;
        }
    }
}
