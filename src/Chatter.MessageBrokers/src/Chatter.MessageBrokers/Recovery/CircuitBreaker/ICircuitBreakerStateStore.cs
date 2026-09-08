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
    /// caller is admitted to and the half-open episode that branch belongs to. It is also the token required to
    /// move the circuit — every outcome is reported back against the admission that authorized the call — so a
    /// caller holds no way to command a transition of its own.
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
        /// Reports one success against the admission that authorized the call. The store adjudicates the report
        /// — a success can only ever CLOSE the circuit, and only a <see cref="CircuitBreakerVerdict.Trial"/>
        /// admission may report one — so the caller never commands the transition. A success reported against an
        /// admission whose episode has ended, or against one the store never issued, records nothing at all.
        /// </summary>
        /// <param name="admission">The admission this outcome is reported against: the one the store issued to this caller.</param>
        /// <param name="successesToClose">How many successes within one half-open episode close the circuit. The
        /// threshold travels with the report rather than being held by the store, so the store carries no
        /// configuration of its own.</param>
        /// <returns><see langword="true"/> only when THIS report is what closed the circuit; <see langword="false"/>
        /// when it was counted without closing, or discarded.</returns>
        Task<bool> RecordSuccessAsync(CircuitBreakerAdmission admission, int successesToClose);

        /// <summary>
        /// Reports one failure against the admission that authorized the call, adjudicated the same way
        /// <see cref="RecordSuccessAsync"/> adjudicates a success: a failure can only ever OPEN the circuit, and
        /// a failure reported against an admission whose episode has ended, or against one that did not authorize
        /// execution, records nothing at all — not even the store's last exception.
        /// </summary>
        /// <param name="admission">The admission this outcome is reported against: the one the store issued to this caller.</param>
        /// <param name="ex">The exception the admitted call failed with. It becomes the store's last exception
        /// only when the report is accepted.</param>
        /// <param name="failuresToOpen">How many failures open the circuit. Like the success threshold, it travels
        /// with the report rather than being held by the store.</param>
        /// <returns><see langword="true"/> only when THIS report is what opened the circuit; <see langword="false"/>
        /// when it was counted without opening, or discarded.</returns>
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
