using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public interface IRecoveryStrategy
    {
        Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken token);
    }
}
