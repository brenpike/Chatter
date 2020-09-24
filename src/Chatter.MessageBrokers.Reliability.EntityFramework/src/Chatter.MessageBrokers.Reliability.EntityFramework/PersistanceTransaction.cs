using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    internal sealed class PersistanceTransaction : IPersistanceTransaction
    {
        private IDbContextTransaction _dbContextTransaction;

        private PersistanceTransaction(IDbContextTransaction dbContextTransaction) 
            => _dbContextTransaction = dbContextTransaction;

        public static PersistanceTransaction Create(IDbContextTransaction dbContextTransaction)
            => new PersistanceTransaction(dbContextTransaction);

        public Guid TransactionId => _dbContextTransaction.TransactionId;

        public Task CommitAsync(CancellationToken cancellationToken = default) 
            => _dbContextTransaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default) 
            => _dbContextTransaction.RollbackAsync(cancellationToken);

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();

            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dbContextTransaction?.Dispose();
            }

            _dbContextTransaction = null;
        }

        async ValueTask DisposeAsyncCore()
        {
            if (!(_dbContextTransaction is null))
            {
                await _dbContextTransaction.DisposeAsync();
            }

            _dbContextTransaction = null;
        }
    }
}
