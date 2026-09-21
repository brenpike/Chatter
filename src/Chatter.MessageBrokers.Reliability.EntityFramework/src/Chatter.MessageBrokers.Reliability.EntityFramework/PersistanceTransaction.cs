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

        // INVARIANT: this is the single commit of the transactions this package intermediates, and it refuses one
        // carrying an inbox claim whose handler did not return. Permission is DERIVED from the claim's own recorded
        // outcome, not from whether an exception reached this point: BrokeredMessageInbox opens every claim
        // unsettled and settles it only when the handler returns, so a caller that swallowed a claimed message's
        // failure and returned normally is refused here just as a caller that rethrew would be. ELIMINATED CLASS: a
        // transaction this package commits while carrying an inbox claim whose handler did not return.
        // The refusal does NOT roll back. Whoever began the transaction rolls it back, which is the ownership rule
        // UnitOfWork.UnitOfWorkTransaction carries.
        // BOUNDARY: a commit issued directly on Database.CurrentTransaction, EF Core's own public handle, reaches
        // no gate here; this package neither withdraws nor intermediates that handle, so the refusal above governs
        // only the commits this package intermediates, not every commit that can reach the store. No test pins
        // that slice; one would construct the bypass and assert the gap. Tracked in issue #513.
        // Oracles: WhenReceivingViaInbox.MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure and
        // .MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed; deleting the refusal below reddens those two
        // facts and no others, measured by deleting it and counting.
        // .MustCommitTheClaimWhenTheHandlerReturns pins the other direction, so a refusal of every commit cannot
        // pass for this one. Rationale in docs/adr/0034-an-unsettled-inbox-claim-withholds-the-commit.md.
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (InboxClaimRegister.HasUnsettledClaim(_dbContextTransaction, out var unsettledClaims))
            {
                throw new InvalidOperationException(
                    $"This transaction refuses to commit because it carries an inbox claim whose handler did not return: {unsettledClaims}. " +
                    $"The claim was flushed into this transaction before the handler ran, so committing now would make it durable and suppress " +
                    $"every redelivery of that message id, even though nothing handled it. A handler failure that was caught and not rethrown " +
                    $"reaches this point looking like success; settle the claim by letting the handler return, or let its failure propagate so " +
                    $"the transaction rolls back and the broker redelivers the message.");
            }

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
