using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    public interface IUnitOfWork
    {
        IPersistanceTransaction CurrentTransaction { get; }
        bool HasActiveTransaction { get; }

        ValueTask<IPersistanceTransaction> BeginAsync(CancellationToken cancellationToken = default);
        Task ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken = default);
        Task CompleteAsync(CancellationToken cancellationToken = default);
    }
}
