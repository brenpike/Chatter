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
        private long _episode;
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

        // INVARIANT: the store is the sole adjudicator of admission, and it decides inside stateLock — which every
        // writer of _state also holds — so a caller never selects its branch from state it observed before an await.
        // It receives a decision the store issued, stamped with the episode that decision belongs to.
        public Task<CircuitBreakerAdmission> AdmitAsync(TimeSpan openToHalfOpenWaitTime)
        {
            var enteredHalfOpen = false;
            CircuitBreakerAdmission admission;

            lock (stateLock)
            {
                if (_state == CircuitBreakerState.Open
                    && DateTime.UtcNow - _lastStateChangedDateUtc >= openToHalfOpenWaitTime)
                {
                    _successCount = 0;
                    _lastStateChangedDateUtc = DateTime.UtcNow;
                    _state = CircuitBreakerState.HalfOpen;
                    _episode++;
                    enteredHalfOpen = true;
                }

                admission = new CircuitBreakerAdmission(VerdictFor(_state), _state, _episode, _lastException);
            }

            if (enteredHalfOpen)
            {
                _logger.LogInformation("Circuit Breaker is now in the HALF-OPEN state.");
            }

            return Task.FromResult(admission);
        }

        private static CircuitBreakerVerdict VerdictFor(CircuitBreakerState state)
        {
            switch (state)
            {
                case CircuitBreakerState.Closed:
                    return CircuitBreakerVerdict.Execute;
                case CircuitBreakerState.HalfOpen:
                    return CircuitBreakerVerdict.Trial;
                default:
                    return CircuitBreakerVerdict.Refused;
            }
        }

        public Task CloseAsync()
        {
            lock (stateLock)
            {
                _lastStateChangedDateUtc = DateTime.UtcNow;
                _state = CircuitBreakerState.Closed;
                _failureCount = 0;
                _episode++;
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
                _episode++;
            }
            _logger.LogInformation("Circuit Breaker is now in the OPEN state.");
            return Task.CompletedTask;
        }

        // INVARIANT: success is progress within ONE half-open episode. Every transition begins a new episode, so
        // a trial that finishes after its own episode ended records nothing rather than crediting a later one.
        public Task<int?> IncrementSuccessCounterAsync(long episode)
        {
            int? successes;
            lock (stateLock)
            {
                successes = _episode == episode ? ++_successCount : (int?)null;
            }

            _logger.LogTrace(successes is null
                ? "Success discarded: the half-open episode it was admitted to has ended"
                : "Incrementing success counter");

            return Task.FromResult(successes);
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
