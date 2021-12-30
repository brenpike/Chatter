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
                await using var transaction = await BeginAsync(ct);
                try
                {
                    transactionContext?.Container.Include(transaction);
                    transactionContext?.Container.Include("CurrentTransactionId", transaction.TransactionId);

                    await operation(ct);
                    await CompleteAsync(ct);
                    _logger.LogTrace($"Unit of work completed successfully.");
                }
                catch (Exception ex)
                {
                    await RollbackAsync(ct);
                    _logger.LogError(ex, "Error occurred during unit of work");
                    throw;
                }
            }, cancellationToken);
        }

        private async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogTrace($"Change(s) saved for context '{typeof(TContext).Name}'.");

            if (!HasActiveTransaction)
            {
                _logger.LogTrace($"There is no active transaction to commit. Skipping.");
                return;
            }

            _logger.LogTrace($"Committing transaction id '{CurrentTransaction.TransactionId}'.");
            await CurrentTransaction.CommitAsync(cancellationToken);
            _logger.LogTrace($"Transaction committed for context '{typeof(TContext).Name}'.");
        }

        private async ValueTask<IPersistanceTransaction> BeginAsync(CancellationToken cancellationToken = default)
        {
            if (HasActiveTransaction)
            {
                _logger.LogTrace($"Cannot create new transaction as the unit of work is already part of transaction id '{CurrentTransaction.TransactionId}'.");
                return CurrentTransaction;
            }

            var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Transaction created for context '{typeof(TContext).Name}' with transaction id '{transaction.TransactionId}'");
            return CurrentTransaction;
        }

        private async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (!HasActiveTransaction)
            {
                _logger.LogTrace($"There is no active transaction - unable to rollback.");
                return;
            }

            _logger.LogTrace($"Rolling back transaction id '{CurrentTransaction.TransactionId}'.");
            await CurrentTransaction.RollbackAsync(cancellationToken);
            _logger.LogTrace($"Transaction rolled back for context '{typeof(TContext).Name}'.");
        }
    }
}
