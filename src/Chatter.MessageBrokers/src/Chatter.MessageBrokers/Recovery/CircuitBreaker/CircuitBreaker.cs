using Chatter.MessageBrokers.Exceptions;
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
        private readonly Timer _timer;
        private readonly ICircuitBreakerStateStore _stateStore;
        private readonly ILogger<CircuitBreaker> _logger;
        private readonly ICircuitBreakerExceptionEvaluator _exceptionEvaluator;
        private readonly SemaphoreSlim _halfOpenSemaphore;

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
                    await Task.Delay(_openToHalfOpenWaitTime, cancellationToken);
                    _logger.LogInformation("Circuit Breaker half-open timer expired. Entering HALF-OPEN state.");
                    await _stateStore.TryHalfOpenAsync();
                    throw new CircuitBreakerOpenException(_stateStore.LastException);
                }

                await _halfOpenSemaphore.WaitAsync(cancellationToken);

                try
                {
                    await _stateStore.TryHalfOpenAsync();
                    var context = await action(_stateStore.State);
                    await TryClose();
                    return context;
                }
                catch (Exception ex)
                {
                    if (ShouldTrip(ex, cancellationToken))
                    {
                        await _stateStore.OpenAsync(ex);
                    }

                    throw;
                }
                finally
                {
                    _halfOpenSemaphore.Release();
                }
            }

            try
            {
                return await action(_stateStore.State);
            }
            catch (Exception ex)
            {
                if (ShouldTrip(ex, cancellationToken))
                {
                    await TryOpen(ex);
                }

                throw;
            }
        }

        // INVARIANT: the single trip-decision site for both the closed path and the half-open trial, so the two
        // can never diverge on which exceptions trip the circuit. A cancellation the caller asked for is that
        // caller shutting down rather than the service failing, so it never trips and never becomes LastException.
        private bool ShouldTrip(Exception ex, CancellationToken cancellationToken)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (!_exceptionEvaluator.ShouldTrip(ex))
            {
                _logger.LogTrace($"Circuit break not configured for exception type '{ex.GetType().FullName}'. Skipping.");
                return false;
            }

            return true;
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
            _logger.LogCritical($"Circuit breaker has been OPEN for {_timeOpenBeforeCriticalFailureNotification} seconds");
        }

        private void ResetOpenTimer() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        private void StartOpenTimer() => _timer?.Change(_timeOpenBeforeCriticalFailureNotification, TimeSpan.FromMilliseconds(-1));
    }
}
