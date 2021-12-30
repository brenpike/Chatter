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

        Task ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken = default);
    }
}
