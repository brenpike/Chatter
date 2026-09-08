using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    /// <summary>
    /// The branch a caller is admitted to. <see cref="Refused"/> is the default value so an admission that
    /// was never issued denies the caller rather than admitting it.
    /// </summary>
    public enum CircuitBreakerVerdict
    {
        Refused,
        Execute,
        Trial
    }

    /// <summary>
    /// The admission an <see cref="ICircuitBreakerStateStore"/> issues to a caller. It is a decision the store
    /// made, not state the caller observed, so it cannot go stale across an await: it names the branch the
    /// caller is admitted to and the half-open episode that branch belongs to.
    /// </summary>
    public readonly struct CircuitBreakerAdmission
    {
        public CircuitBreakerAdmission(CircuitBreakerVerdict verdict, CircuitBreakerState state, long episode, Exception lastException)
        {
            Verdict = verdict;
            State = state;
            Episode = episode;
            LastException = lastException;
        }

        public CircuitBreakerVerdict Verdict { get; }
        public CircuitBreakerState State { get; }

        /// <summary>
        /// Identifies the state the store was in when the admission was issued. Every transition begins a new
        /// episode, so progress recorded under an admission whose episode has ended belongs to no episode.
        /// </summary>
        public long Episode { get; }
        public Exception LastException { get; }
    }

    public interface ICircuitBreakerStateStore
    {
        Exception LastException { get; }
        DateTime LastStateChangedDateUtc { get; }

        /// <summary>
        /// Reports one outcome against the admission it was issued and returns whether THIS report transitioned
        /// the circuit. The store adjudicates the report — a success can only ever CLOSE and a failure can only
        /// ever OPEN — so the caller never commands a transition and an outcome reported against an ended
        /// episode or an admission that was never issued records nothing at all.
        /// </summary>
        Task<bool> RecordSuccessAsync(CircuitBreakerAdmission admission, int successesToClose);

        /// <inheritdoc cref="RecordSuccessAsync"/>
        Task<bool> RecordFailureAsync(CircuitBreakerAdmission admission, Exception ex, int failuresToOpen);

        /// <summary>
        /// Adjudicates one caller's admission against the store's own state, transitioning an open circuit whose
        /// cooling period has elapsed into the half-open state as part of the same decision.
        /// </summary>
        Task<CircuitBreakerAdmission> AdmitAsync(TimeSpan openToHalfOpenWaitTime);
        bool IsClosed { get; }
        CircuitBreakerState State { get; }
        int FailureCount { get; }
        int SuccessCount { get; }
    }
}
