#nullable enable annotations
using System;
using System.Collections.Concurrent;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The #361 OPT-IN escape from a deterministically-failing Outbox Document. The relay's fail-closed bias — throw, so
    /// the change feed never checkpoints, so the document re-surfaces — is correct for a TRANSIENT publish failure, but a
    /// document that fails on EVERY pass re-throws forever: the lease never advances and every later document in that
    /// partition range never drains. This policy carries the threshold at which a document has failed CONSECUTIVELY often
    /// enough to be given up on, the non-pending status value it is then stamped with (which
    /// <see cref="CosmosOutboxDocument.IsPendingOutbox"/> no longer admits, so the lease can advance past it), and the
    /// BOUNDED per-document-id consecutive-failure counting that decides when the threshold is reached.
    /// </summary>
    /// <remarks>
    /// OFF BY DEFAULT: a threshold of zero is the OFF switch — counting never advances and no document is ever elected, so
    /// a relay that never opted in behaves exactly as it did before this policy existed. The tracked-id set is CAPPED at
    /// <see cref="TrackedDocumentCapacity"/>: past the cap a NEW document id is simply not tracked, so a long-lived relay
    /// under transient failures degrades to the fail-closed behavior rather than to unbounded memory. Constructing a policy
    /// that could never move a document out of pending is rejected here, so an unsafe policy is unconstructable rather than
    /// merely validated.
    /// </remarks>
    internal sealed class OutboxPoisonPolicy
    {
        /// <summary>
        /// The maximum number of document ids whose consecutive-failure counts are tracked at once. A new id arriving past
        /// this cap is NOT tracked, so it can never be elected — the memory the policy costs is bounded by construction.
        /// </summary>
        public const int TrackedDocumentCapacity = 1024;

        // INVARIANT: an entry exists ONLY for a document whose most recent drain FAILED and whose count has not yet
        // reached the threshold. A successful drain removes the entry, and an elected document removes its own entry (it
        // is stamped non-pending, so the change feed never hands it back), so neither can leak a tracked id.
        private readonly ConcurrentDictionary<string, int> _consecutiveFailuresByDocumentId = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        public OutboxPoisonPolicy(int poisonAfterConsecutiveFailures, string poisonStatusValue)
        {
            if (poisonAfterConsecutiveFailures < 0)
            {
                throw new ArgumentException(
                    $"The consecutive-failure threshold cannot be negative (got {poisonAfterConsecutiveFailures}); use 0 to leave the poison policy off.",
                    nameof(poisonAfterConsecutiveFailures));
            }

            if (poisonAfterConsecutiveFailures > 0)
            {
                // An enabled policy MUST be able to move a given-up document out of pending, so its status value cannot be
                // empty nor equal the pending status — either would leave the document admitted forever, which is the very
                // head-of-line stall the policy exists to end.
                if (string.IsNullOrEmpty(poisonStatusValue))
                {
                    throw new ArgumentException(
                        "A poison status value is required and cannot be empty when the poison policy is enabled.", nameof(poisonStatusValue));
                }

                if (string.Equals(poisonStatusValue, CosmosOutboxDocument.StatusPending, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"The poison status value cannot equal the pending status '{CosmosOutboxDocument.StatusPending}'; a given-up document must be advanced OUT of pending or it would re-surface on the change feed forever.",
                        nameof(poisonStatusValue));
                }
            }

            PoisonAfterConsecutiveFailures = poisonAfterConsecutiveFailures;
            PoisonStatusValue = poisonStatusValue;
        }

        /// <summary>The number of CONSECUTIVE failures of one document id after which that document is given up on. Zero is off.</summary>
        public int PoisonAfterConsecutiveFailures { get; }

        /// <summary>The non-pending status value a given-up document is stamped with. Never read while the policy is off.</summary>
        public string PoisonStatusValue { get; }

        /// <summary>Whether the policy is armed at all. A relay whose policy is off re-throws every failure, as it always did.</summary>
        public bool IsEnabled => PoisonAfterConsecutiveFailures > 0;

        /// <summary>
        /// The policy with no threshold — counting never advances and no document is ever elected. The default, and what
        /// <see cref="OutboxDeliverySettings.Legacy"/> carries, so the registry-driven relay is behaviorally untouched.
        /// </summary>
        // INVARIANT: safe to share as a singleton BECAUSE it is off: a disabled policy tracks nothing, so it holds no
        // per-relay state that two relays could contend over.
        public static OutboxPoisonPolicy Disabled { get; } = new OutboxPoisonPolicy(poisonAfterConsecutiveFailures: 0, poisonStatusValue: null);

        /// <summary>
        /// Records one FAILED drain of <paramref name="documentId"/> and reports whether that document has now failed
        /// consecutively often enough to be given up on. Returns false while the policy is off, while the document is
        /// still below the threshold, and for a new document id arriving past <see cref="TrackedDocumentCapacity"/>.
        /// </summary>
        public bool RecordFailure(string documentId)
        {
            if (!IsEnabled || string.IsNullOrEmpty(documentId))
            {
                return false;
            }

            // The cap refuses only ids the policy is not ALREADY tracking, so a document mid-way to the threshold still
            // advances. Concurrent drains may admit a few ids beyond the cap between this read and the write below; the
            // overshoot is bounded by the number of drains in flight, which is what the cap exists to bound.
            if (!_consecutiveFailuresByDocumentId.ContainsKey(documentId)
                && _consecutiveFailuresByDocumentId.Count >= TrackedDocumentCapacity)
            {
                return false;
            }

            int consecutiveFailures = _consecutiveFailuresByDocumentId.AddOrUpdate(documentId, 1, (_, failures) => failures + 1);
            if (consecutiveFailures < PoisonAfterConsecutiveFailures)
            {
                return false;
            }

            _consecutiveFailuresByDocumentId.TryRemove(documentId, out _);
            return true;
        }

        /// <summary>
        /// Records one SUCCESSFUL drain of <paramref name="documentId"/>, clearing its consecutive-failure count so an
        /// intermittent failure can never accumulate across successful drains into a give-up.
        /// </summary>
        public void RecordSuccess(string documentId)
        {
            if (!IsEnabled || string.IsNullOrEmpty(documentId))
            {
                return;
            }

            _consecutiveFailuresByDocumentId.TryRemove(documentId, out _);
        }
    }
}
