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
                // INVARIANT: the transaction is begun outside the try so that a failure to begin one propagates with
                // no scope in existence to clean up. On the failure path both terminal steps - rolling back and
                // disposing - are guarded, so neither can replace the exception that caused the failure. On the
                // success path the disposal is deliberately left unguarded: there is no causal exception to mask,
                // and swallowing there would hide a real commit-time failure.
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
                    await CleanUpAsync(() => RollbackAsync(scope, CancellationToken.None), "roll back").ConfigureAwait(false);
                    await CleanUpAsync(() => scope.DisposeAsync().AsTask(), "dispose").ConfigureAwait(false);
                    _logger.LogError(ex, "Error occurred during unit of work");
                    throw;
                }

                await scope.DisposeAsync().ConfigureAwait(false);
            }, cancellationToken);
        }

        // INVARIANT: cleanup runs under CancellationToken.None. Honouring the token that failed the unit of work
        // would make a cancelled operation skip its own rollback and leave the transaction open.
        private async Task CleanUpAsync(Func<Task> cleanUp, string cleanUpDescription)
        {
            try
            {
                await cleanUp().ConfigureAwait(false);
            }
            catch (Exception cleanUpException)
            {
                _logger.LogWarning(cleanUpException, "Failed to {CleanUpDescription} the unit of work's transaction while handling an earlier failure. The earlier failure is the one reported.", cleanUpDescription);
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
