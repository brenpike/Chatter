using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    public interface IRetryStrategy
    {
        TResult Execute<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken = default)
#if !NETSTANDARD2_0
            => ExecuteAsync(action, cancellationToken).GetAwaiter().GetResult()
#endif
            ;
        Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken = default);

    }
}
