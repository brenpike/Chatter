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

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (!HasActiveTransaction)
            {
                _logger.LogTrace($"Cannot complete unit of work. There is no active transaction.");
                return;
            }

            var transaction = CurrentTransaction;
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogTrace($"Change saved for context '{typeof(TContext).Name}'. Transaction id '{transaction.TransactionId}'.");
            await transaction.CommitAsync(cancellationToken);
            _logger.LogTrace($"Transaction committed for context '{typeof(TContext).Name}'. Transaction id '{transaction.TransactionId}'.");
        }

        public async ValueTask<IPersistanceTransaction> BeginAsync(CancellationToken cancellationToken = default)
        {
            if (HasActiveTransaction)
            {
                _logger.LogTrace($"Cannot create new unit of work as a the current unit of work has not completed.");
                return CurrentTransaction;
            }

            var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Unit of work created for context '{typeof(TContext).Name}' with transaction id '{transaction.TransactionId}'");
            return CurrentTransaction;
        }

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
                    await transaction.RollbackAsync(ct);
                    _logger.LogError(ex, "Error occurred during unit of work");
                    throw;
                }
            }, cancellationToken);
        }
    }
}
