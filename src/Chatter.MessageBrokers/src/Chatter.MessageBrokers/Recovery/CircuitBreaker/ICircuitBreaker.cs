using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public interface ICircuitBreaker
    {
        bool IsClosed { get; }
        bool IsOpen { get; }

        Task<TResult> ExecuteAsync<TResult>(Func<CircuitBreakerState, Task<TResult>> action, CancellationToken cancellationToken = default);
    }
}
