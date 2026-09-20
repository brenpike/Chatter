using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    // INVARIANT: this is what a unit of work hands back when its context has no transaction, so that the absence of
    // a transaction is reported rather than dereferenced. It is never placed in a TransactionContext.Container -
    // only a live handle is - so an application that took a transaction out of the container still holds a real one.
    internal sealed class NoActiveTransaction : IPersistanceTransaction
    {
        public static readonly NoActiveTransaction Instance = new NoActiveTransaction();

        private NoActiveTransaction()
        { }

        public Guid TransactionId => Guid.Empty;

        public Task CommitAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("There is no active transaction to commit. Begin a transaction, or run the work through the unit of work's ExecuteAsync, before committing.");

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("There is no active transaction to roll back. Begin a transaction, or run the work through the unit of work's ExecuteAsync, before rolling back.");

        // INVARIANT: disposing the absence of a transaction is not an error. An application that disposes whatever
        // CurrentTransaction handed it must not have to ask whether a transaction was active first.
        public void Dispose()
        { }

        public ValueTask DisposeAsync() => default;
    }
}
