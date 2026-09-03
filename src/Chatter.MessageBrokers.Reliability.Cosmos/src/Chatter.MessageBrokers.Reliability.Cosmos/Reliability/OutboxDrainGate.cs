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
    /// CONCURRENCY: the SDK delivers ONE lease's batches SERIALLY to the processor that owns it, so per-entry state
    /// needs no locking. The dictionary is nonetheless concurrent because this processor's DISTINCT leases are
    /// delivered concurrently. Both statements hold under the per-processor scoping invariant above, and only under it:
    /// a gate shared across processors has no serial delivery guarantee per entry.
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

        /// <summary>Builds a gate on the system clock, which is what a host uses.</summary>
        internal OutboxDrainGate(GuardedRelayLog log)
            : this(log, TimeProvider.System)
        {
        }

        /// <summary>
        /// Builds a gate on a supplied <paramref name="timeProvider"/>, the seam a test drives the suspension window
        /// through without a wall-clock wait.
        /// </summary>
        internal OutboxDrainGate(GuardedRelayLog log, TimeProvider timeProvider)
        {
            _log = log;
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        /// <summary>
        /// Consults the gate for ONE change-feed batch on <paramref name="leaseToken"/>, returning whether that batch
        /// may publish. A suspended lease is refused for the whole window; once the window elapses, exactly the batch
        /// that consults next is let through as the half-open probe and that consult is what consumes it.
        /// </summary>
        internal bool PermitDrain(string leaseToken)
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
            state.SuspendedAtTimestamp = _timeProvider.GetTimestamp();
            return true;
        }

        /// <summary>
        /// Records one Confirmation Failure on <paramref name="leaseToken"/> — a document that PUBLISHED and could
        /// not be marked delivered — raising the Drain Suspension at the threshold and re-arming the window on every
        /// failure past it.
        /// </summary>
        internal void RecordConfirmationFailure(string leaseToken, Exception confirmationFailure)
        {
            LeaseDrainState state = _statesByLeaseToken.GetOrAdd(leaseToken, _ => new LeaseDrainState());
            state.ConsecutiveConfirmationFailures++;

            if (state.ConsecutiveConfirmationFailures < Threshold)
            {
                return;
            }

            // Re-arm FIRST, so a probe whose confirmation failed again waits another full window. It happens on both
            // arms: opening a suspension and re-arming an open one both start the window from now.
            state.SuspendedAtTimestamp = _timeProvider.GetTimestamp();

            if (state.IsSuspended)
            {
                // Already open. The window is re-armed above and the failure is counted, but the suspension is not
                // raised a second time: it never lifted, so there is no new suspension to report.
                return;
            }

            state.IsSuspended = true;

            // INVARIANT: no outer ADR-0010 R1 off-guard is needed here, and adding one would be noise: the only
            // argument is the lease token the caller already holds, so nothing is BUILT before the emit method's own
            // instrument guard runs, and the tag is built inside that guard.
            CosmosReliabilityDiagnostics.RecordDrainSuspension(leaseToken);

            // ALWAYS-ON, at Error, through the guarded sink: a meter-less application has no other channel, and a
            // relay that silently stopped publishing a lease would be indistinguishable from one with nothing to
            // publish. The confirmation fault rides along because it is the defect an operator has to go and fix.
            _log.Error(confirmationFailure,
                       "The Cosmos Outbox Relay suspended draining lease {LeaseToken} after {ConfirmationFailureCount} consecutive confirmation failures. Its messages published but could not be marked delivered, so every redrain republished them; draining resumes once a confirmation succeeds.",
                       leaseToken,
                       state.ConsecutiveConfirmationFailures);
        }

        /// <summary>
        /// Records that a confirmation SUCCEEDED on <paramref name="leaseToken"/>, lifting any Drain Suspension on it
        /// and EVICTING its entry.
        /// </summary>
        internal void RecordConfirmationSuccess(string leaseToken)
        {
            // EVICTION is what closes the prior art's unbounded-key-space property BY CONSTRUCTION. An entry exists
            // only while its lease is CURRENTLY failing, so the key space is bounded by the leases this host
            // concurrently owns AND that are failing - not by a configured capacity that could fill up, silently stop
            // counting, and disengage the very brake it was added to arm. There is deliberately no capacity, no
            // eviction policy and no entry time-to-live here; success is the whole lifetime rule.
            if (!_statesByLeaseToken.TryRemove(leaseToken, out LeaseDrainState state) || !state.IsSuspended)
            {
                return;
            }

            // Reported only for a lease that WAS suspended. A healthy lease confirms on every batch it drains, so
            // reporting each one would drown the suspension report this exists to close.
            _log.Information("The Cosmos Outbox Relay resumed draining lease {LeaseToken}: a confirmation succeeded, so its documents are being marked delivered again.", leaseToken);
        }

        // The per-lease state. Entries exist only for leases CURRENTLY failing.
        private sealed class LeaseDrainState
        {
            internal int ConsecutiveConfirmationFailures;
            internal bool IsSuspended;
            internal long SuspendedAtTimestamp;
        }
    }
}
