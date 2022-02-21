using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public interface ICircuitBreakerStateStore
    {
        Exception LastException { get; }
        DateTime LastStateChangedDateUtc { get; }
        Task OpenAsync(Exception ex);
        Task<int> IncrementFailureCounterAsync(Exception ex);
        Task<int> IncrementSuccessCounterAsync();
        Task CloseAsync();
        Task HalfOpenAsync();
        bool IsClosed { get; }
        CircuitBreakerState State { get; }
        int FailureCount { get; }
        int SuccessCount { get; }
    }
}
