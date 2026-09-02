#nullable enable annotations
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The configurable carrier for the #222 change-feed outbox relay's drain knobs: an OPTIONAL predicate that can only
    /// further NARROW which pending documents the relay admits, the field paths/values it stamps on a delivered
    /// document, and the opt-in #361 <see cref="OutboxPoisonPolicy"/> deciding when a deterministically-failing document is
    /// given up on. The unsafe representations the seam could previously construct are now UNCONSTRUCTABLE: the #222
    /// id-guard (<see cref="CosmosOutboxDocument.IsPendingOutbox"/>) is composed INSIDE <see cref="IsAdmitted"/> so no
    /// construction path can replace or weaken it (F1, closed-by-construction), and the constructor REJECTS every
    /// delivered-stamp configuration that would fail to move a document out of <c>pending</c> (F2). <see cref="Legacy"/>
    /// reproduces the relay's original hard-coded behavior byte-for-byte, so a relay constructed without explicit settings
    /// is indistinguishable from the pre-seam relay.
    /// </summary>
    internal sealed class OutboxDeliverySettings
    {
        private readonly Func<JsonElement, bool>? _additionalPendingFilter;

        public OutboxDeliverySettings(int deliveredTtlSeconds,
                                      string statusPatchPath,
                                      string deliveredStatusValue,
                                      Func<JsonElement, bool>? additionalPendingFilter = null,
                                      int poisonAfterConsecutiveFailures = 0,
                                      string? poisonStatusValue = null)
        {
            // F2 (a): a delivered document MUST advance out of pending, so its status value cannot be empty nor equal the
            // pending status — otherwise it would re-surface on the change feed forever.
            if (string.IsNullOrEmpty(deliveredStatusValue))
            {
                throw new ArgumentException(
                    "A delivered status value is required and cannot be empty.", nameof(deliveredStatusValue));
            }

            if (string.Equals(deliveredStatusValue, CosmosOutboxDocument.StatusPending, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The delivered status value cannot equal the pending status '{CosmosOutboxDocument.StatusPending}'; a delivered document must be advanced OUT of pending or it would re-surface on the change feed forever.",
                    nameof(deliveredStatusValue));
            }

            // F2 (b): the delivered TTL must be a positive number of seconds. 0 and all negatives (including -1, Cosmos'
            // "retain indefinitely") are rejected — "delivered but retained" is out of scope; a delivered document must be
            // scheduled for self-purge.
            if (deliveredTtlSeconds <= 0)
            {
                throw new ArgumentException(
                    $"The delivered TTL must be a positive number of seconds (got {deliveredTtlSeconds}); 'delivered but retained' (0, -1, or any negative) is out of scope.",
                    nameof(deliveredTtlSeconds));
            }

            // F2 (c): the status patch path must be a valid JSON pointer AND anchored to the SAME field the always-applied
            // pending gate reads. This is what makes "a status patch that does not move the document out of pending"
            // structurally impossible rather than merely validated: a path that targeted any other field would advance a
            // field the gate never inspects and leave the document pending forever.
            string anchoredStatusPath = "/" + CosmosOutboxDocument.StatusField;
            if (!IsValidJsonPointer(statusPatchPath))
            {
                throw new ArgumentException(
                    $"The status patch path '{statusPatchPath}' is not a valid JSON pointer; it must start with '/' and contain at least one non-empty segment.",
                    nameof(statusPatchPath));
            }

            if (!string.Equals(statusPatchPath, anchoredStatusPath, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The status patch path must be '{anchoredStatusPath}' — it is anchored to the '{CosmosOutboxDocument.StatusField}' field the always-applied pending gate reads. A status patch targeting any other path would not move the document out of pending, so it would re-surface on the change feed forever.",
                    nameof(statusPatchPath));
            }

            // F2 (d): the #361 poison policy. OutboxPoisonPolicy's own constructor rejects a negative threshold and — while
            // enabled — an empty or equal-to-pending poison status; the ONE invariant only this type can see is checked
            // here, because a give-up stamped with the DELIVERED value would be indistinguishable from an actual delivery.
            var poisonPolicy = new OutboxPoisonPolicy(poisonAfterConsecutiveFailures, poisonStatusValue);
            if (poisonPolicy.IsEnabled && string.Equals(poisonPolicy.PoisonStatusValue, deliveredStatusValue, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The poison status value cannot equal the delivered status value '{deliveredStatusValue}'; a document the relay gave up on must stay distinguishable from one it actually delivered.",
                    nameof(poisonStatusValue));
            }

            // The delivered TTL is NOT configurable here: it is always stamped at the Cosmos-reserved "/ttl" path
            // (CosmosOutboxDocument.TtlField) — the only field Cosmos honors for self-purge — so a non-purging delivered
            // stamp is unrepresentable rather than merely validated.
            DeliveredTtlSeconds = deliveredTtlSeconds;
            StatusPatchPath = statusPatchPath;
            DeliveredStatusValue = deliveredStatusValue;
            PoisonPolicy = poisonPolicy;
            _additionalPendingFilter = additionalPendingFilter;
        }

        /// <summary>The post-delivery retention window stamped on a delivered document, in seconds.</summary>
        public int DeliveredTtlSeconds { get; }

        /// <summary>The Cosmos document-patch path for the delivery status field.</summary>
        public string StatusPatchPath { get; }

        /// <summary>The status value a delivered document is advanced to.</summary>
        public string DeliveredStatusValue { get; }

        /// <summary>
        /// The VALIDATED #361 poison policy: the consecutive-failure threshold at which the relay gives up on a
        /// deterministically-failing document, the non-pending status it is then stamped with, and the bounded
        /// per-document-id failure counting behind it. Disabled unless the caller opted in.
        /// </summary>
        public OutboxPoisonPolicy PoisonPolicy { get; }

        /// <summary>
        /// Admits a change-feed document as a pending outbox document to drain. The built-in #222 id-guard
        /// (<see cref="CosmosOutboxDocument.IsPendingOutbox"/>) is composed INSIDE this method so no construction path can
        /// omit it: an optional caller-supplied filter can only further NARROW admission (logical AND), never widen or
        /// replace the id-guard.
        /// </summary>
        public bool IsAdmitted(JsonElement document)
            => CosmosOutboxDocument.IsPendingOutbox(document)
               && (_additionalPendingFilter is null || _additionalPendingFilter(document));

        /// <summary>
        /// The settings reproducing the relay's original hard-coded behavior: a one-day delivered TTL, the
        /// <c>/status</c> -&gt; <c>delivered</c> stamp (the delivered TTL is always stamped at the hard-wired
        /// <c>/ttl</c> path), no additional pending filter (the always-applied
        /// <see cref="CosmosOutboxDocument.IsPendingOutbox"/> id-guard is the sole admission gate), and a DISABLED poison
        /// policy — a legacy relay keeps the fail-closed behavior and can never give up on a document.
        /// </summary>
        public static OutboxDeliverySettings Legacy { get; } = new OutboxDeliverySettings(
            deliveredTtlSeconds: 86400,
            statusPatchPath: "/" + CosmosOutboxDocument.StatusField,
            deliveredStatusValue: CosmosOutboxDocument.StatusDelivered,
            additionalPendingFilter: null);

        /// <summary>
        /// Maps a <see cref="CosmosOutboxRelayOptions"/>' three stamp knobs, its optional additional pending filter, and its
        /// opt-in poison policy into the validating constructor. The single builder reused by the standalone host and
        /// registration-time validation, so every construction path goes through the same F2 invariant enforcement.
        /// </summary>
        internal static OutboxDeliverySettings FromOptions(CosmosOutboxRelayOptions options)
        {
            _ = options ?? throw new ArgumentNullException(nameof(options));
            return new OutboxDeliverySettings(
                options.DeliveredTtlSeconds,
                options.StatusPatchPath,
                options.DeliveredStatusValue,
                options.AdditionalPendingFilter,
                options.PoisonAfterConsecutiveFailures,
                options.PoisonStatusValue);
        }

        // A valid JSON pointer for the relay's status patch path: starts with '/' and has at least one non-empty segment (so a
        // null/empty path, a path without a leading '/', and the root pointer "/" are all rejected).
        private static bool IsValidJsonPointer(string pointer)
        {
            if (string.IsNullOrEmpty(pointer) || pointer[0] != '/')
            {
                return false;
            }

            foreach (string segment in pointer.Split('/'))
            {
                if (segment.Length > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
