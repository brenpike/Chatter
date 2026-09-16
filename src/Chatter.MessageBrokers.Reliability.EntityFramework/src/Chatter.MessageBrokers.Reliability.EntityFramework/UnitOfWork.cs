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

        public IPersistanceTransaction CurrentTransaction => PersistanceTransaction.Create(_context.Database.CurrentTransaction);

        public bool HasActiveTransaction => _context?.Database?.CurrentTransaction != null;

        public Task ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            return strategy.ExecuteAsync(async ct =>
            {
                await using var scope = await BeginAsync(ct);
                try
                {
                    transactionContext?.Container.Include<IPersistanceTransaction>(scope.Transaction);
                    transactionContext?.Container.Include("CurrentTransactionId", scope.Transaction.TransactionId);

                    await operation(ct);
                    await CompleteAsync(scope, ct);
                    _logger.LogTrace($"Unit of work completed successfully.");
                }
                catch (Exception ex)
                {
                    await RollbackAsync(scope, ct);
                    _logger.LogError(ex, "Error occurred during unit of work");
                    throw;
                }
            }, cancellationToken);
        }

        private async Task CompleteAsync(UnitOfWorkTransaction scope, CancellationToken cancellationToken = default)
        {
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogTrace($"Change(s) saved for context '{typeof(TContext).Name}'.");

            if (!scope.BegunHere)
            {
                _logger.LogTrace($"Transaction id '{scope.Transaction.TransactionId}' was begun by the caller. Leaving it open for its owner to commit.");
                return;
            }

            _logger.LogTrace($"Committing transaction id '{scope.Transaction.TransactionId}'.");
            await scope.CommitAsync(cancellationToken);
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
            await scope.RollbackAsync(cancellationToken);
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
