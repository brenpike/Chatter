using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public interface ICircuitBreakerStateStore
    {
        Exception LastException { get; }
        DateTime LastStateChangedDateUtc { get; }
        Task Open(Exception ex);
        Task<int> IncrementFailureCounter(Exception ex);
        Task<int> IncrementSuccessCounter();
        Task Close();
        Task HalfOpen();
        bool IsClosed { get; }
        CircuitBreakerState State { get; }
        int FailureCount { get; }
        int SuccessCount { get; }
    }
}
