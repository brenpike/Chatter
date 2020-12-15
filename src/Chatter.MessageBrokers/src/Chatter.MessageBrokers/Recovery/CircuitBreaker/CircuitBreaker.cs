using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public sealed class CircuitBreaker : ICircuitBreaker
    {
        private readonly TimeSpan _openToHalfOpenWaitTime;
        private readonly int _concurrentHalfOpenAttempts;
        private readonly int _numberOfFailuresBeforeOpen;
        private readonly int _numberOfHalfOpenSuccessesToClose;
        private readonly TimeSpan _timeOpenBeforeCriticalFailureNotification;
        private Timer _timer;
        private readonly ICircuitBreakerStateStore _stateStore;
        private readonly ILogger<CircuitBreaker> _logger;
        private readonly ICriticalFailureNotifier _criticalFailureNotifier;
        private bool _disposedValue;
        private SemaphoreSlim _halfOpenSemaphore;

        public CircuitBreaker(ICircuitBreakerStateStore stateStore,
                              CircuitBreakerOptions circuitBreakerOptions,
                              ILogger<CircuitBreaker> logger,
                              ICriticalFailureNotifier criticalFailureNotifier)
        {
            if (circuitBreakerOptions is null)
            {
                throw new ArgumentNullException(nameof(circuitBreakerOptions));
            }

            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _criticalFailureNotifier = criticalFailureNotifier ?? throw new ArgumentNullException(nameof(criticalFailureNotifier));
            _openToHalfOpenWaitTime = TimeSpan.FromSeconds(circuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds);
            _concurrentHalfOpenAttempts = circuitBreakerOptions.ConcurrentHalfOpenAttempts;
            _numberOfFailuresBeforeOpen = circuitBreakerOptions.NumberOfFailuresBeforeOpen;
            _numberOfHalfOpenSuccessesToClose = circuitBreakerOptions.NumberOfHalfOpenSuccessesToClose;
            _timeOpenBeforeCriticalFailureNotification = TimeSpan.FromSeconds(circuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification);
            _halfOpenSemaphore = new SemaphoreSlim(_concurrentHalfOpenAttempts, _concurrentHalfOpenAttempts);
            _timer = new Timer(CriticalFailureNotification);
        }

        public bool IsClosed { get { return _stateStore.IsClosed; } }
        public bool IsOpen { get { return !IsClosed; } }

        public async Task Execute(Func<CircuitBreakerState, Task> action, CancellationToken cancellationToken = default)
        {
            if (IsOpen)
            {
                if (_stateStore.State == CircuitBreakerState.HalfOpen ||
                    _stateStore.LastStateChangedDateUtc + _openToHalfOpenWaitTime < DateTime.UtcNow)
                {
                    _logger.LogDebug("Circuit Breaker timeout timer expired. Entering HALF-OPEN state.");
                    try
                    {
                        await _halfOpenSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        await _stateStore.HalfOpen();
                        await action(_stateStore.State).ConfigureAwait(false);
                        await TryClose();
                        return;
                    }
                    catch (Exception ex)
                    {
                        await _stateStore.Open(ex);
                        throw;
                    }
                    finally
                    {
                        _halfOpenSemaphore.Release();
                    }
                }

                return;
            }

            try
            {
                await action(_stateStore.State).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await TryOpen(ex);
                throw;
            }
        }

        private async Task TryClose()
        {
            _logger.LogDebug("Attempting to CLOSE circuit");
            if (await _stateStore.IncrementSuccessCounter() >= _numberOfHalfOpenSuccessesToClose)
            {
                ResetOpenTimer();
                await _stateStore.Close();
            }
        }

        private async Task TryOpen(Exception ex)
        {
            _logger.LogDebug("Attempting to OPEN circuit");
            if (await _stateStore.IncrementFailureCounter(ex).ConfigureAwait(false) >= _numberOfFailuresBeforeOpen)
            {
                StartOpenTimer();
                await _stateStore.Open(ex).ConfigureAwait(false);
            }
        }

        private async void CriticalFailureNotification(object state)
        {
            var reason = $"Circuit breaker has been OPEN for {_timeOpenBeforeCriticalFailureNotification} seconds";
            var failureContext = new FailureContext(null, null, reason, _stateStore.LastException, _stateStore.FailureCount, null);
            await _criticalFailureNotifier.Notify(failureContext).ConfigureAwait(false);
            StartOpenTimer();
        }

        private void ResetOpenTimer() => _timer.Change(Timeout.Infinite, Timeout.Infinite);
        private void StartOpenTimer() => _timer.Change(_timeOpenBeforeCriticalFailureNotification, TimeSpan.FromMilliseconds(-1));

        private void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _halfOpenSemaphore?.Dispose();
                    _timer?.Dispose();
                }

                _halfOpenSemaphore = null;
                _timer = null;
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
