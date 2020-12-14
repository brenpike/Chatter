using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public interface ICircuitBreaker
    {
        bool IsClosed { get; }
        bool IsOpen { get; }

        Task Execute(Func<CircuitBreakerState, Task> action, CancellationToken cancellationToken = default);
    }
}
