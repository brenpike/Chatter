using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
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

        public bool HasActiveTransaction => CurrentTransaction != null;

        public async Task CompleteAsync()
        {
            if (!HasActiveTransaction)
            {
                _logger.LogTrace($"Cannot complete unit of work. There is not active transaction.");
                return;
            }

            var transaction = CurrentTransaction;
            await _context.SaveChangesAsync();
            _logger.LogTrace($"Change saved for context '{typeof(TContext).Name}'. Transaction id '{transaction.TransactionId}'.");
            await transaction.CommitAsync();
            _logger.LogTrace($"Transaction committed for context '{typeof(TContext).Name}'. Transaction id '{transaction.TransactionId}'.");
        }

        public async ValueTask<IPersistanceTransaction> BeginAsync()
        {
            if (HasActiveTransaction)
            {
                _logger.LogTrace($"Cannot create new unit of work as a the current unit of work has not completed.");
                return CurrentTransaction;
            }

            var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            _logger.LogTrace($"Unit of work created for context '{typeof(TContext).Name}' with transaction id '{transaction.TransactionId}'");
            return CurrentTransaction;
        }
    }
}
