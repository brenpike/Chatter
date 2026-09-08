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

        // INVARIANT: the branch is named by the admission the store ISSUES, and the decision is taken exactly
        // once per call. Nothing here is selected from state this caller observed, so no await can invalidate
        // the branch it is on: an admission is a decision, not an observation.
        public async Task<TResult> ExecuteAsync<TResult>(Func<CircuitBreakerState, Task<TResult>> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var admission = await _stateStore.AdmitAsync(_openToHalfOpenWaitTime);

            if (admission.Verdict == CircuitBreakerVerdict.Refused)
            {
                // The wait paces the refusal. The receive loop has no pacing of its own and the default retry
                // delay strategy is NoDelayRetry, so returning the refusal immediately would busy-spin.
                await Task.Delay(_openToHalfOpenWaitTime, cancellationToken);
                throw new CircuitBreakerOpenException(admission.LastException);
            }

            if (admission.Verdict == CircuitBreakerVerdict.Trial)
            {
                return await ExecuteTrialAsync(action, admission, cancellationToken);
            }

            try
            {
                return await action(admission.State);
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

        private async Task<TResult> ExecuteTrialAsync<TResult>(Func<CircuitBreakerState, Task<TResult>> action,
                                                              CircuitBreakerAdmission admission,
                                                              CancellationToken cancellationToken)
        {
            _logger.LogInformation("Circuit Breaker admitted a HALF-OPEN trial.");
            await _halfOpenSemaphore.WaitAsync(cancellationToken);

            try
            {
                var context = await action(admission.State);
                await TryClose(admission.Episode);
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

        private async Task TryClose(long episode)
        {
            _logger.LogTrace("Attempting to CLOSE circuit");
            var successes = await _stateStore.IncrementSuccessCounterAsync(episode);

            if (successes is null)
            {
                _logger.LogTrace("Half-open trial finished after its episode ended. Its success was discarded.");
                return;
            }

            if (successes.Value >= _numberOfHalfOpenSuccessesToClose)
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
