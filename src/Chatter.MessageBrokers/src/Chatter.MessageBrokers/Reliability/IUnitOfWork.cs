using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    /// <summary>
    /// Relational-only: the ambient-transaction tier's unit-of-work. The document tier never implements this
    /// interface; its atomic-write initiation is the document-tier batch-lifecycle behavior (a sibling), not a
    /// faked <see cref="ExecuteAsync"/>.
    /// </summary>
    public interface IUnitOfWork
    {
        IPersistanceTransaction CurrentTransaction { get; }
        bool HasActiveTransaction { get; }

        Task ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken = default);
    }
}
