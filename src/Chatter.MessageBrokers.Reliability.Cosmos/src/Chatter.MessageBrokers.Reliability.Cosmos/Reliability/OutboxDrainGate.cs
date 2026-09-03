using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using System;
using System.Collections.Concurrent;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The per-PROCESSOR Drain Suspension gate: the Outbox Relay consults it before draining a change-feed batch and
    /// declines to publish a Lease Token whose confirmations keep failing (#416). The publish succeeds, the delivered
    /// stamp does not, the batch is never checkpointed, and the same document republishes on every pass — real broker
    /// traffic, real receiver work and real request units, indefinitely.
    /// </summary>
    /// <remarks>
    /// WHY STOP DRAINING RATHER THAN MARK THE DOCUMENT. A give-up decision cannot be recorded in a store that is not
    /// accepting writes, and the write that is failing IS a write to that store. Every per-document marking scheme has
    /// that floor, wherever the count is kept. So the answer is to stop DRAINING: the suspension requires NO
    /// successful write.
    /// INVARIANT: it HALTS NOTHING. No <c>ChangeFeedProcessor</c> is stopped and no hosted service is stopped. The
    /// relay simply declines to publish, and the host takes the module's EXISTING fail-closed exit — throw out of the
    /// change-feed handler, the SDK does not checkpoint, the batch re-surfaces.
    /// WHY CONSECUTIVE FAILURES ON ONE LEASE rather than distinct document identities. The relay is fail-closed, so it
    /// never advances past the first failing document in a batch: batch [A,B,C] publishes A, fails to stamp A, throws,
    /// and re-surfaces as [A,B,C] forever. Only A is ever seen, so a distinctness requirement would never be satisfied
    /// in exactly the single-lease case this exists for, and the suspension would never be raised.
    /// INVARIANT: <see cref="Threshold"/> and <see cref="SuspensionWindow"/> are NON-CONFIGURABLE, and that is a
    /// decision rather than an omission — do not add a knob. They are a SAFETY BOUND, not a tuning parameter: a
    /// consumer who tuned the threshold to infinity would re-open the very defect this closes. The registry-driven
    /// host carries no options object at all, so a knob would also be asymmetric across the two hosts. And
    /// non-configurability is itself closed-by-construction: a bound that cannot be configured cannot be
    /// misconfigured into uselessness.
    /// INVARIANT: ONE gate per PROCESSOR, never one per host. A gate instance is reachable ONLY from its own
    /// processor's change-feed handler, so it never sees a Lease Token belonging to another Change-Feed Source
    /// Identity — and THAT is what makes the Lease Token a SUFFICIENT key here. A Lease Token is a partition-key-range
    /// id of the container the batch came from ("0", "1", ...), so two sources on one host routinely report the SAME
    /// token: a host-shared gate would collapse them onto one entry, letting one source's confirmation success evict
    /// another source's failure count and one source's suspension refuse another source's batch.
    /// The gate carries its <see cref="SourceIdentity"/> for the SAME reason, one layer out: the Lease Token suffices
    /// as an internal key precisely BECAUSE the gate is per-processor, but a measurement LEAVING this process has no
    /// such scoping, so the identity that was implicit here is stated explicitly on every suspension it reports.
    /// It is a construction-time requirement, so a gate that could report an ambiguous suspension is not constructible.
    /// CONCURRENCY: a state TRANSITION is CLOSED BY CONSTRUCTION rather than by an external ordering guarantee. A
    /// <c>LeaseDrainState</c> is IMMUTABLE, and each of the THREE transition writes — the probe re-arm in
    /// <see cref="PermitDrain"/> and both arms of <see cref="RecordConfirmationFailure"/> — is a compare-and-swap
    /// against the exact snapshot the transition was computed from, so a transition computed from ONE observation of
    /// gate state can never be applied to a DIFFERENT one. The FOURTH write is NOT one of them: the eviction in
    /// <see cref="RecordConfirmationSuccess"/> is an atomic remove-and-return rather than a swap against a snapshot,
    /// and the residual that leaves is named on that method.
    /// Three sub-classes are thereby unrepresentable: a lost or torn multi-field update; a write applied to an entry a
    /// concurrent confirmation success already evicted; and a side effect emitted without its justifying transition
    /// having landed. THOSE THREE hold under any interleaving, so they do not rest on a claim about how the SDK
    /// schedules delivery — a claim Chatter neither owns nor enforces, and which a future SDK change, a retry wrapper,
    /// or a host that ever fanned one lease out could otherwise silently void. That is the scope of the claim and not
    /// a global one: the ENTITLEMENT of a suspension lift remains order-sensitive, as
    /// <see cref="RecordConfirmationSuccess"/> states.
    /// The swap loop is LOCK-FREE rather than wait-free: a caller that loses a swap re-reads and recomputes. Under
    /// real delivery it sees no contention, because the concurrency the dictionary exists for is this processor's
    /// DISTINCT leases arriving at DIFFERENT keys.
    /// The window is measured on the <see cref="TimeProvider"/>'s MONOTONIC timestamp rather than a wall clock, so a
    /// clock step can neither collapse a suspension nor extend one indefinitely.
    /// </remarks>
    internal sealed class OutboxDrainGate
    {
        /// <summary>
        /// How many consecutive Confirmation Failures on one Lease Token raise a Drain Suspension. NON-CONFIGURABLE
        /// by construction; see the type's remarks for why a knob here would be a defect.
        /// </summary>
        internal const int Threshold = 5;

        /// <summary>
        /// How long a Drain Suspension holds before ONE probe batch is let through. NON-CONFIGURABLE by construction;
        /// see the type's remarks.
        /// </summary>
        internal static readonly TimeSpan SuspensionWindow = TimeSpan.FromSeconds(60);

        private readonly ConcurrentDictionary<string, LeaseDrainState> _statesByLeaseToken = new ConcurrentDictionary<string, LeaseDrainState>(StringComparer.Ordinal);
        private readonly GuardedRelayLog _log;
        private readonly TimeProvider _timeProvider;

        /// <summary>
        /// Builds a gate for ONE Change-Feed Source Identity on the system clock, which is what a host uses.
        /// </summary>
        internal OutboxDrainGate(string sourceIdentity, GuardedRelayLog log)
            : this(sourceIdentity, log, TimeProvider.System)
        {
        }

        /// <summary>
        /// Builds a gate on a supplied <paramref name="timeProvider"/>, the seam a test drives the suspension window
        /// through without a wall-clock wait.
        /// </summary>
        internal OutboxDrainGate(string sourceIdentity, GuardedRelayLog log, TimeProvider timeProvider)
        {
            SourceIdentity = sourceIdentity ?? throw new ArgumentNullException(nameof(sourceIdentity));
            _log = log;
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        /// <summary>
        /// The Change-Feed Source Identity this gate belongs to — the processor name — reported alongside every Lease
        /// Token this gate's processor measures, so a token named "0" stays attributable to the source that drained it.
        /// </summary>
        /// <remarks>
        /// INVARIANT: it is a CONSTRUCTION-TIME requirement with no identity-less overload, so a gate whose suspensions
        /// report under a fabricated or absent identity is not constructible. The change-feed handler reads the
        /// identity OFF THE GATE it was handed rather than being passed a second copy, which is what keeps the
        /// one-gate-per-processor invariant the single carrier of this fact.
        /// </remarks>
        internal string SourceIdentity { get; }

        /// <summary>
        /// Consults the gate for ONE change-feed batch on <paramref name="leaseToken"/>, returning whether that batch
        /// may publish. A suspended lease is refused for the whole window; once the window elapses, exactly the batch
        /// that consults next is let through as the half-open probe and that consult is what consumes it.
        /// </summary>
        internal bool PermitDrain(string leaseToken)
        {
            while (true)
            {
                if (!_statesByLeaseToken.TryGetValue(leaseToken, out LeaseDrainState state) || !state.IsSuspended)
                {
                    return true;
                }

                if (_timeProvider.GetElapsedTime(state.SuspendedAtTimestamp) < SuspensionWindow)
                {
                    return false;
                }

                // The window elapsed, so EXACTLY THIS batch is let through as the probe that discovers whether the
                // confirmation path came back. Re-arming here is what makes it exactly this one: the next consult is
                // refused again unless a confirmation has meanwhile succeeded and evicted the lease outright.
                if (_statesByLeaseToken.TryUpdate(leaseToken, state.Rearmed(_timeProvider.GetTimestamp()), state))
                {
                    return true;
                }

                // INVARIANT: the re-arming swap is what OWNS the probe, so losing it means another caller took the
                // probe or a confirmation evicted the lease. Re-decide against a fresh snapshot rather than permit:
                // an evicted lease permits on the next pass, a probe already taken is refused, and either way exactly
                // one batch per window is let through.
            }
        }

        /// <summary>
        /// Records one Confirmation Failure on <paramref name="leaseToken"/> — a document that PUBLISHED and could
        /// not be marked delivered — raising the Drain Suspension at the threshold and re-arming the window on every
        /// failure past it.
        /// </summary>
        internal void RecordConfirmationFailure(string leaseToken, Exception confirmationFailure)
        {
            LeaseDrainState priorState;
            LeaseDrainState nextState;

            while (true)
            {
                long timestamp = _timeProvider.GetTimestamp();

                if (!_statesByLeaseToken.TryGetValue(leaseToken, out priorState))
                {
                    nextState = BuildFailureTransition(priorState: null, timestamp);

                    if (_statesByLeaseToken.TryAdd(leaseToken, nextState))
                    {
                        break;
                    }

                    continue;
                }

                nextState = BuildFailureTransition(priorState, timestamp);

                if (_statesByLeaseToken.TryUpdate(leaseToken, nextState, priorState))
                {
                    break;
                }

                // INVARIANT: a lost swap means the snapshot this transition was computed from is stale — another
                // failure landed, or a confirmation success evicted the lease outright. Re-read and RECOMPUTE, so the
                // count that lands is always exactly one more than the state it was derived from and the side effects
                // below fire against a transition that provably landed.
            }

            // The suspension OPENED on this call only if the snapshot the winning swap consumed was not already
            // suspended. Reported once per opening: a suspension that never lifted is not a new suspension, so a
            // failure against an already-open one re-arms the window and counts, and reports nothing.
            bool suspensionOpened = !(priorState?.IsSuspended ?? false) && nextState.IsSuspended;

            if (!suspensionOpened)
            {
                return;
            }

            // INVARIANT: no outer ADR-0010 R1 off-guard is needed here, and adding one would be noise: both arguments
            // are strings this gate already holds - the lease token the caller passed and the source identity fixed at
            // construction - so nothing is BUILT before the emit method's own instrument guard runs, and the tags are
            // built inside that guard.
            CosmosReliabilityDiagnostics.RecordDrainSuspension(SourceIdentity, leaseToken);

            // ALWAYS-ON, at Error, through the guarded sink: a meter-less application has no other channel, and a
            // relay that silently stopped publishing a lease would be indistinguishable from one with nothing to
            // publish. The confirmation fault rides along because it is the defect an operator has to go and fix.
            _log.Error(confirmationFailure,
                       "The Cosmos Outbox Relay suspended draining lease {LeaseToken} after {ConfirmationFailureCount} consecutive confirmation failures. Its messages published but could not be marked delivered, so every redrain republished them; draining resumes once a confirmation succeeds.",
                       leaseToken,
                       nextState.ConsecutiveConfirmationFailures);
        }

        /// <summary>
        /// The ONE Drain Suspension transition rule, applied by BOTH arms of <see cref="RecordConfirmationFailure"/> —
        /// the first failure on a lease and every failure after it — so a suspension is computed identically however
        /// the entry got there. A <see langword="null"/> <paramref name="priorState"/> is the absent entry.
        /// </summary>
        /// <remarks>
        /// Re-arming happens on BOTH arms: opening a suspension and re-arming an open one both start the window from
        /// now, so a probe whose confirmation failed again waits another full window.
        /// INVARIANT: the below-threshold arm is never suspended, and that is total rather than assumed — a suspension
        /// lifts ONLY by eviction, so a present entry that is suspended already carries at least
        /// <see cref="Threshold"/> failures and can only transition to more. <c>SuspendedAtTimestamp</c> is meaningless
        /// on that arm, which is why every reader guards on <c>IsSuspended</c> first.
        /// </remarks>
        private static LeaseDrainState BuildFailureTransition(LeaseDrainState priorState, long timestamp)
        {
            int consecutiveConfirmationFailures = (priorState?.ConsecutiveConfirmationFailures ?? 0) + 1;

            if (consecutiveConfirmationFailures < Threshold)
            {
                return new LeaseDrainState(consecutiveConfirmationFailures, isSuspended: false, suspendedAtTimestamp: 0);
            }

            return new LeaseDrainState(consecutiveConfirmationFailures, isSuspended: true, suspendedAtTimestamp: timestamp);
        }

        /// <summary>
        /// Records that a confirmation SUCCEEDED on <paramref name="leaseToken"/>, lifting any Drain Suspension on it
        /// and EVICTING its entry. It requires the <paramref name="receipt"/> the drain's status write produced: with
        /// no receipt present this returns having done NOTHING.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the receipt is a REQUIREMENT rather than a hint, and it is what makes a gate transition inferred
        /// from control-flow ARRIVAL unrepresentable. A drain that merely reached its end proves only that nothing
        /// threw, and absence of failure is not presence of success: an EMPTY batch and a batch every document's
        /// pending-outbox pre-gate rejected both complete having performed no confirming write, and the monitored
        /// container is CO-RESIDENT by design, so the second is the ORDINARY batch. A <see cref="ConfirmationReceipt"/>
        /// is derivable only from an object a status write returned, so no caller can express a lift it did not earn.
        /// The undeliverable give-up stamp counts as evidence: it is the same write, at the same status path, whose
        /// failure is what opens a suspension. It cannot lift one falsely because the relay is fail-closed — a
        /// Confirmation Failure anywhere in the batch throws before any receipt reaches this method.
        /// EVICTION on a present receipt is UNCHANGED, and remains what bounds the key space.
        /// RESIDUAL, stated plainly: a confirmation success may clear failure state recorded AFTER its own confirming
        /// write landed. It is reachable ONLY when two change-feed delegate invocations for the SAME lease overlap
        /// across a lease rebalance. It CANNOT fire within a single batch: the relay is fail-closed, so a Confirmation
        /// Failure throws before this method is reached, on BOTH hosts. The cost is one counter reset — at most
        /// <see cref="Threshold"/> republishes, then the suspension re-opens — and the reset-path arithmetic already
        /// bounds it: ADR-0007 names host restart, lease rebalance and entry eviction on a successful confirmation as
        /// reset paths each costing at most <see cref="Threshold"/> republishes before re-opening. This residual is
        /// INSIDE that shipped bound, so it is a documented characteristic rather than an unbounded hole.
        /// It is an ENTITLEMENT property and NOT a torn write: <c>TryRemove(key, out value)</c> is ATOMIC and returns
        /// exactly the entry it removed, so the <c>IsSuspended</c> check decides against exactly what it cleared and
        /// there is no observe-then-act window here to close.
        /// </remarks>
        internal void RecordConfirmationSuccess(string leaseToken, ConfirmationReceipt receipt)
        {
            if (!receipt.IsPresent)
            {
                return;
            }

            // EVICTION is what closes the prior art's unbounded-key-space property BY CONSTRUCTION. An entry exists
            // only while its lease is CURRENTLY failing, so the key space is bounded by the leases this host
            // concurrently owns AND that are failing - not by a configured capacity that could fill up, silently stop
            // counting, and disengage the very brake it was added to arm. There is deliberately no capacity, no
            // eviction policy and no entry time-to-live here; success is the whole lifetime rule.
            //
            // TryRemove is ATOMIC and hands back exactly the entry it cleared, so the IsSuspended check below decides
            // against precisely that entry - there is no observe-then-act window. What it does NOT order is
            // ENTITLEMENT: this success may clear failure state recorded AFTER its own confirming write landed,
            // reachable only when two same-lease delegate invocations overlap across a lease rebalance. It cannot fire
            // within a batch - the relay is fail-closed and throws first. Cost is one counter reset: at most Threshold
            // republishes before the suspension re-opens, which is inside the reset-path bound ADR-0007 already names.
            if (!_statesByLeaseToken.TryRemove(leaseToken, out LeaseDrainState state) || !state.IsSuspended)
            {
                return;
            }

            // Reported only for a lease that WAS suspended. A healthy lease confirms on every batch it drains, so
            // reporting each one would drown the suspension report this exists to close.
            _log.Information("The Cosmos Outbox Relay resumed draining lease {LeaseToken}: a confirmation succeeded, so its documents are being marked delivered again.", leaseToken);
        }

        // The per-lease state. Entries exist only for leases CURRENTLY failing.
        // INVARIANT: IMMUTABLE, and deliberately a plain sealed CLASS - NOT a record, NOT a struct, and with no
        // Equals, GetHashCode or == of its own. EqualityComparer<LeaseDrainState>.Default is therefore REFERENCE
        // IDENTITY, which is what makes ConcurrentDictionary.TryUpdate a TRUE compare-and-swap: the swap matches the
        // exact instance a transition was computed from, not merely one that carries the same content. Structural
        // equality here would let a stale snapshot match a different instance and land a transition never observed.
        private sealed class LeaseDrainState
        {
            internal readonly int ConsecutiveConfirmationFailures;
            internal readonly bool IsSuspended;
            internal readonly long SuspendedAtTimestamp;

            internal LeaseDrainState(int consecutiveConfirmationFailures, bool isSuspended, long suspendedAtTimestamp)
            {
                ConsecutiveConfirmationFailures = consecutiveConfirmationFailures;
                IsSuspended = isSuspended;
                SuspendedAtTimestamp = suspendedAtTimestamp;
            }

            // The half-open probe's re-arm: the same count and suspension, windowed from a new timestamp.
            internal LeaseDrainState Rearmed(long suspendedAtTimestamp)
                => new LeaseDrainState(ConsecutiveConfirmationFailures, IsSuspended, suspendedAtTimestamp);
        }
    }
}
