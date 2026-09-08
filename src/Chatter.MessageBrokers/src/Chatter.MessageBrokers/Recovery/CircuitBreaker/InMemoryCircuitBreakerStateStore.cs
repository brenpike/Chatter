using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
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
        private TimeSpan _lastStateChangedElapsed;
        private CircuitBreakerState _state;
        private long _episode;
        private readonly ILogger<InMemoryCircuitBreakerStateStore> _logger;
        private readonly object stateLock = new object();

        // The cooling period is an ELAPSED interval, so it is measured from a monotonic source rather than from
        // the wall clock: an NTP correction, a VM migration or an operator moving the system clock would
        // otherwise admit a trial early or hold the circuit open past its configured wait.
        private readonly Stopwatch _sinceConstruction = Stopwatch.StartNew();

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

        // INVARIANT: the store is the sole adjudicator of EVERY transition. A transition happens only inside
        // stateLock — which every writer of _state also holds — and only as the store's own adjudication of an
        // admission or of an outcome reported against one; there is no transition command for a caller to issue.
        // So a caller neither selects its branch from state it observed before an await nor moves the circuit
        // from one: it receives a decision the store issued, stamped with the episode that decision belongs to.
        //
        // INVARIANT: this store's bodies are synchronous, so the caller's cancellation is honoured by refusing
        // BEFORE stateLock is taken. Checking it inside the lock would abandon a half-applied decision, and
        // awaiting on it under the lock is forbidden outright. The parameter is on the contract for an external
        // store whose adjudication is real I/O.
        public Task<CircuitBreakerAdmission> AdmitAsync(TimeSpan openToHalfOpenWaitTime, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var enteredHalfOpen = false;
            CircuitBreakerAdmission admission;

            lock (stateLock)
            {
                if (_state == CircuitBreakerState.Open
                    && _sinceConstruction.Elapsed - _lastStateChangedElapsed >= openToHalfOpenWaitTime)
                {
                    _successCount = 0;
                    StampStateChange();
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

        // INVARIANT: the store adjudicates the report and performs the transition it warrants inside ONE
        // stateLock acquisition, so validating the admission and acting on it can never be interleaved. A
        // success is progress within ONE half-open episode: a trial that finishes after its own episode ended
        // records nothing rather than closing a circuit another caller has since tripped.
        public Task<bool> RecordSuccessAsync(CircuitBreakerAdmission admission, int successesToClose, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var discarded = false;
            var closed = false;

            lock (stateLock)
            {
                if (admission.Verdict != CircuitBreakerVerdict.Trial || admission.Episode != _episode)
                {
                    discarded = true;
                }
                else if (++_successCount >= successesToClose)
                {
                    // The success count is deliberately NOT reset here: entering half-open is what starts an
                    // episode's count, and AdmitAsync already resets it there.
                    StampStateChange();
                    _state = CircuitBreakerState.Closed;
                    _failureCount = 0;
                    _episode++;
                    closed = true;
                }
            }

            if (discarded)
            {
                _logger.LogTrace("Success discarded: the half-open episode it was admitted to has ended");
            }
            else if (closed)
            {
                _logger.LogInformation("Circuit Breaker is now in the CLOSED state.");
            }
            else
            {
                _logger.LogTrace("Incrementing success counter");
            }

            return Task.FromResult(closed);
        }

        // INVARIANT: a failed TRIAL re-opens the circuit immediately without counting toward failuresToOpen —
        // the trial IS the probe, so one failure is the whole evidence. That is a rule the STORE derives from
        // the admission's verdict, never a branch the caller chose.
        public Task<bool> RecordFailureAsync(CircuitBreakerAdmission admission, Exception ex, int failuresToOpen, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var discarded = false;
            var opened = false;

            // A verdict is AUTHORIZED to report a failure, never merely not-forbidden: a verdict added later
            // reports nothing until it is named here.
            var authorized = admission.Verdict == CircuitBreakerVerdict.Execute
                             || admission.Verdict == CircuitBreakerVerdict.Trial;

            lock (stateLock)
            {
                if (!authorized || admission.Episode != _episode)
                {
                    discarded = true;
                }
                else
                {
                    _lastException = ex;

                    if (admission.Verdict == CircuitBreakerVerdict.Trial)
                    {
                        OpenTheCircuit();
                        opened = true;
                    }
                    else if (++_failureCount >= failuresToOpen)
                    {
                        OpenTheCircuit();
                        opened = true;
                    }
                }
            }

            if (discarded)
            {
                _logger.LogTrace("Failure discarded: the episode it was admitted to has ended");
            }
            else if (opened)
            {
                _logger.LogInformation("Circuit Breaker is now in the OPEN state.");
            }
            else
            {
                _logger.LogTrace("Incrementing failure counter");
            }

            return Task.FromResult(opened);
        }

        // INVARIANT: called only while stateLock is held.
        private void OpenTheCircuit()
        {
            StampStateChange();
            _state = CircuitBreakerState.Open;
            _episode++;
        }

        // INVARIANT: called only while stateLock is held, and it is the ONLY assignment site for either stamp.
        // The two are set together here so a transition site cannot move one and leave the other behind: the
        // wall-clock stamp is the published diagnostic, and the monotonic one is what every elapsed decision
        // reads. No decision reads DateTime.UtcNow.
        private void StampStateChange()
        {
            _lastStateChangedDateUtc = DateTime.UtcNow;
            _lastStateChangedElapsed = _sinceConstruction.Elapsed;
        }
    }
}
