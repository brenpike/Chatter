using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public sealed class InMemoryCircuitBreakerStateStore : ICircuitBreakerStateStore
    {
        private int _failureCount;
        private int _successCount;
        private readonly ILogger<InMemoryCircuitBreakerStateStore> _logger;

        public InMemoryCircuitBreakerStateStore(ILogger<InMemoryCircuitBreakerStateStore> logger)
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public Exception LastException { get; private set; }
        public DateTime LastStateChangedDateUtc { get; private set; }
        public bool IsClosed => State == CircuitBreakerState.Closed;
        public CircuitBreakerState State { get; private set; }
        public int FailureCount => _failureCount;
        public int SuccessCount => _successCount;

        public Task HalfOpen()
        {
            if (State == CircuitBreakerState.HalfOpen)
            {
                return Task.CompletedTask;
            }

            Interlocked.Exchange(ref _successCount, 0);
            LastStateChangedDateUtc = DateTime.UtcNow;
            State = CircuitBreakerState.HalfOpen;
            _logger.LogDebug("Circuit Breaker is now in the HALF-OPEN state.");
            return Task.CompletedTask;
        }

        public Task Close()
        {
            LastStateChangedDateUtc = DateTime.UtcNow;
            State = CircuitBreakerState.Closed;
            Interlocked.Exchange(ref _failureCount, 0);
            _logger.LogDebug("Circuit Breaker is now in the CLOSED state.");
            return Task.CompletedTask;
        }

        public Task Open(Exception ex)
        {
            LastStateChangedDateUtc = DateTime.UtcNow;
            LastException = ex;
            State = CircuitBreakerState.Open;
            _logger.LogDebug("Circuit Breaker is now in the OPEN state.");
            return Task.CompletedTask;
        }

        public Task<int> IncrementSuccessCounter()
        {
            _logger.LogDebug("Incrementing success counter");
            return Task.FromResult(Interlocked.Increment(ref _successCount));
        }

        public Task<int> IncrementFailureCounter(Exception ex)
        {
            LastException = ex;
            _logger.LogDebug("Incrementing failure counter");
            return Task.FromResult(Interlocked.Increment(ref _failureCount));
        }
    }
}
