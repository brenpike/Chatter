using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    public interface IUnitOfWork
    {
        IPersistanceTransaction CurrentTransaction { get; }
        bool HasActiveTransaction { get; }

        ValueTask<IPersistanceTransaction> BeginAsync();
        Task ExecuteAsync(Func<Task> operation, TransactionContext transactionContext);
        Task CompleteAsync();
    }
}
