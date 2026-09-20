using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    // INVARIANT: a PersistanceTransaction always wraps a transaction. Creation refuses a null one and commit and
    // rollback refuse once the wrapped transaction has been released, so no member of this type can dereference a
    // transaction it does not hold. Callers with no transaction to wrap get NoActiveTransaction instead.
    internal sealed class PersistanceTransaction : IPersistanceTransaction
    {
        private IDbContextTransaction _dbContextTransaction;
        private bool _disposed;

        private PersistanceTransaction(IDbContextTransaction dbContextTransaction)
            => _dbContextTransaction = dbContextTransaction;

        public static PersistanceTransaction Create(IDbContextTransaction dbContextTransaction)
            => new PersistanceTransaction(dbContextTransaction ?? throw new ArgumentNullException(nameof(dbContextTransaction)));

        public Guid TransactionId => _dbContextTransaction?.TransactionId ?? Guid.Empty;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _dbContextTransaction.CommitAsync(cancellationToken);
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _dbContextTransaction.RollbackAsync(cancellationToken);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);

            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        void Dispose(bool disposing)
        {
            _disposed = true;

            if (disposing)
            {
                _dbContextTransaction?.Dispose();
            }

            _dbContextTransaction = null;
        }

        // INVARIANT: the wrapped transaction is released from the field before it is awaited, so a disposal that
        // throws still leaves this handle disposed rather than holding a transaction it can no longer use.
        async ValueTask DisposeAsyncCore()
        {
            _disposed = true;

            var dbContextTransaction = _dbContextTransaction;
            _dbContextTransaction = null;

            if (!(dbContextTransaction is null))
            {
                await dbContextTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
