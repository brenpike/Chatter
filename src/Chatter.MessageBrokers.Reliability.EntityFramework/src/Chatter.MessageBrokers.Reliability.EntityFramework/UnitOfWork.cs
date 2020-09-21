using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    public class UnitOfWork<TContext> : IUnitOfWork where TContext : DbContext
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
                return;
            }

            await _context.SaveChangesAsync();
            await CurrentTransaction.CommitAsync();
        }

        public async ValueTask<IPersistanceTransaction> BeginAsync()
        {
            if (HasActiveTransaction)
            {
                return CurrentTransaction;
            }

            var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            return PersistanceTransaction.Create(transaction);
        }
    }
}
