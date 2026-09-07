using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    // INVARIANT: every read and every write of every published member of this store occurs inside
    // stateLock. Logging stays outside it, and no await ever occurs under it.
    public sealed class InMemoryCircuitBreakerStateStore : ICircuitBreakerStateStore
    {
        private int _failureCount;
        private int _successCount;
        private Exception _lastException;
        private DateTime _lastStateChangedDateUtc;
        private CircuitBreakerState _state;
        private readonly ILogger<InMemoryCircuitBreakerStateStore> _logger;
        private readonly object stateLock = new object();

        public InMemoryCircuitBreakerStateStore(ILogger<InMemoryCircuitBreakerStateStore> logger)
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public Exception LastException
        {
            get { lock (stateLock) { return _lastException; } }
        }

        public DateTime LastStateChangedDateUtc
        {
            get { lock (stateLock) { return _lastStateChangedDateUtc; } }
        }

        public bool IsClosed
        {
            get { lock (stateLock) { return _state == CircuitBreakerState.Closed; } }
        }

        public CircuitBreakerState State
        {
            get { lock (stateLock) { return _state; } }
        }

        public int FailureCount
        {
            get { lock (stateLock) { return _failureCount; } }
        }

        public int SuccessCount
        {
            get { lock (stateLock) { return _successCount; } }
        }

        // INVARIANT: the store is the sole adjudicator of the HALF-OPEN transition. The compare-and-swap runs
        // inside stateLock, which every writer of State also holds, so a caller can never command the
        // transition from a state it read before an unbounded await — it can only ask, and be refused.
        public Task<bool> TryHalfOpenAsync()
        {
            lock (stateLock)
            {
                if (_state != CircuitBreakerState.Open)
                {
                    return Task.FromResult(false);
                }

                _successCount = 0;
                _lastStateChangedDateUtc = DateTime.UtcNow;
                _state = CircuitBreakerState.HalfOpen;
            }

            _logger.LogInformation("Circuit Breaker is now in the HALF-OPEN state.");
            return Task.FromResult(true);
        }

        public Task CloseAsync()
        {
            lock (stateLock)
            {
                _lastStateChangedDateUtc = DateTime.UtcNow;
                _state = CircuitBreakerState.Closed;
                _failureCount = 0;
            }
            _logger.LogInformation("Circuit Breaker is now in the CLOSED state.");
            return Task.CompletedTask;
        }

        public Task OpenAsync(Exception ex)
        {
            lock (stateLock)
            {
                _lastStateChangedDateUtc = DateTime.UtcNow;
                _lastException = ex;
                _state = CircuitBreakerState.Open;
            }
            _logger.LogInformation("Circuit Breaker is now in the OPEN state.");
            return Task.CompletedTask;
        }

        public Task<int> IncrementSuccessCounterAsync()
        {
            _logger.LogTrace("Incrementing success counter");
            lock (stateLock)
            {
                return Task.FromResult(++_successCount);
            }
        }

        public Task<int> IncrementFailureCounterAsync(Exception ex)
        {
            _logger.LogTrace("Incrementing failure counter");
            lock (stateLock)
            {
                _lastException = ex;
                return Task.FromResult(++_failureCount);
            }
        }
    }
}
