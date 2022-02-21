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
        private readonly ICircuitBreakerExceptionEvaluator _exceptionEvaluator;
        private bool _disposedValue;
        private SemaphoreSlim _halfOpenSemaphore;

        public CircuitBreaker(ICircuitBreakerStateStore stateStore,
                              CircuitBreakerOptions circuitBreakerOptions,
                              ILogger<CircuitBreaker> logger,
                              ICircuitBreakerExceptionEvaluator exceptionEvaluator)
        {
            if (circuitBreakerOptions is null)
            {
                throw new ArgumentNullException(nameof(circuitBreakerOptions));
            }

            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionEvaluator = exceptionEvaluator ?? throw new ArgumentNullException(nameof(exceptionEvaluator));
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

        public async Task<TResult> ExecuteAsync<TResult>(Func<CircuitBreakerState, Task<TResult>> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsOpen)
            {
                if (_stateStore.State != CircuitBreakerState.HalfOpen)
                {
                    await Task.Delay(_openToHalfOpenWaitTime);
                    _logger.LogInformation("Circuit Breaker half-open timer expired. Entering HALF-OPEN state.");
                }

                try
                {
                    await _halfOpenSemaphore.WaitAsync(cancellationToken);
                    await _stateStore.HalfOpenAsync();
                    var context = await action(_stateStore.State);
                    await TryClose();
                    return context;
                }
                catch (Exception ex)
                {
                    await _stateStore.OpenAsync(ex);
                    throw;
                }
                finally
                {
                    try
                    {
                        _halfOpenSemaphore?.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                throw new CircuitBreakerOpenException(_stateStore.LastException);
            }

            try
            {
                return await action(_stateStore.State);
            }
            catch (Exception ex)
            {
                if (!_exceptionEvaluator.ShouldTrip(ex))
                {
                    _logger.LogTrace($"Circuit break not configured for exception type '{ex.GetType().FullName}'. Skipping.");
                    throw;
                }

                await TryOpen(ex);
                throw;
            }
        }

        private async Task TryClose()
        {
            _logger.LogTrace("Attempting to CLOSE circuit");
            if (await _stateStore.IncrementSuccessCounterAsync() >= _numberOfHalfOpenSuccessesToClose)
            {
                await _stateStore.CloseAsync();
                ResetOpenTimer();
            }
        }

        private async Task TryOpen(Exception ex)
        {
            _logger.LogTrace("Attempting to OPEN circuit");
            if (await _stateStore.IncrementFailureCounterAsync(ex) >= _numberOfFailuresBeforeOpen)
            {
                await _stateStore.OpenAsync(ex);
                StartOpenTimer();
            }
        }

        private void CriticalFailureNotification(object state)
        {
            _logger.LogWarning($"Circuit breaker has been OPEN for {_timeOpenBeforeCriticalFailureNotification} seconds");
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
