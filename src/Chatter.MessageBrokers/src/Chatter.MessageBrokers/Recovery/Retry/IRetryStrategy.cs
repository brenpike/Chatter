using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    public interface IRetryStrategy
    {
        TResult Execute<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken = default) => ExecuteAsync(action, cancellationToken).GetAwaiter().GetResult();
        Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken = default);

    }
}
