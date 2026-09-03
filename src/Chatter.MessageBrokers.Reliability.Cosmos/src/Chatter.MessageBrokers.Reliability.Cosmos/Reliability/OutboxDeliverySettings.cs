#nullable enable annotations
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The configurable carrier for the #222 change-feed outbox relay's drain knobs: an OPTIONAL predicate that can only
    /// further NARROW which pending documents the relay admits, the field paths/values it stamps on a delivered
    /// document, and the <see cref="OutboxGiveUpPolicy"/> deciding when a document that fails on every pass is given up
    /// on — its opt-in #361 pre-publish arm and its always-on post-publish arm. The unsafe representations the seam could
    /// previously construct are now UNCONSTRUCTABLE: the #222 id-guard
    /// (<see cref="CosmosOutboxDocument.IsPendingOutbox"/>) is composed INSIDE <see cref="IsAdmitted"/> so no
    /// construction path can replace or weaken it (F1, closed-by-construction), and the constructor REJECTS every
    /// delivered-stamp configuration that would fail to move a document out of <c>pending</c> (F2).
    /// </summary>
    internal sealed class OutboxDeliverySettings
    {
        private readonly Func<JsonElement, bool>? _additionalPendingFilter;

        public OutboxDeliverySettings(int deliveredTtlSeconds,
                                      string statusPatchPath,
                                      string deliveredStatusValue,
                                      Func<JsonElement, bool>? additionalPendingFilter = null,
                                      int poisonAfterConsecutiveFailures = 0,
                                      string? poisonStatusValue = null,
                                      int giveUpAfterUnconfirmedPublishes = OutboxGiveUpPolicy.DefaultGiveUpAfterUnconfirmedPublishes,
                                      string? unconfirmedStatusValue = null)
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

            // F2 (d): the #361 poison arm. OutboxGiveUpPolicy's own constructor rejects a negative threshold and — while
            // enabled — an empty or equal-to-pending poison status; the ONE invariant only this type can see is checked
            // here, because a give-up stamped with the DELIVERED value would be indistinguishable from an actual delivery.
            var giveUpPolicy = new OutboxGiveUpPolicy(
                poisonAfterConsecutiveFailures,
                poisonStatusValue,
                giveUpAfterUnconfirmedPublishes,
                unconfirmedStatusValue ?? CosmosOutboxDocument.StatusUnconfirmed);

            if (giveUpPolicy.IsPoisonEnabled && string.Equals(giveUpPolicy.PoisonStatusValue, deliveredStatusValue, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The poison status value cannot equal the delivered status value '{deliveredStatusValue}'; a document the relay gave up on must stay distinguishable from one it actually delivered.",
                    nameof(poisonStatusValue));
            }

            // F2 (e): the always-on post-publish arm, mirroring F2 (d) but UNCONDITIONAL — the brake has no off switch, so
            // its status value is always reachable and is therefore always validated. The policy's own constructor rejects
            // a non-positive threshold plus an empty or equal-to-pending status; the two invariants only this type can see
            // are checked here, because a published-but-unconfirmed stamp carrying the DELIVERED value would claim a
            // confirmation nobody got, and one carrying the POISON value would claim the message never went out.
            if (string.Equals(giveUpPolicy.UnconfirmedStatusValue, deliveredStatusValue, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The published-but-unconfirmed status value cannot equal the delivered status value '{deliveredStatusValue}'; a delivery nobody could confirm must stay distinguishable from one the relay watched land.",
                    nameof(unconfirmedStatusValue));
            }

            if (giveUpPolicy.IsPoisonEnabled && string.Equals(giveUpPolicy.UnconfirmedStatusValue, giveUpPolicy.PoisonStatusValue, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The published-but-unconfirmed status value cannot equal the poison status value '{giveUpPolicy.PoisonStatusValue}'; a message that WAS published must never be recorded as one that was never delivered.",
                    nameof(unconfirmedStatusValue));
            }

            // The delivered TTL is NOT configurable here: it is always stamped at the Cosmos-reserved "/ttl" path
            // (CosmosOutboxDocument.TtlField) — the only field Cosmos honors for self-purge — so a non-purging delivered
            // stamp is unrepresentable rather than merely validated.
            DeliveredTtlSeconds = deliveredTtlSeconds;
            StatusPatchPath = statusPatchPath;
            DeliveredStatusValue = deliveredStatusValue;
            UnconfirmedStatusValue = giveUpPolicy.UnconfirmedStatusValue;
            GiveUpPolicy = giveUpPolicy;
            _additionalPendingFilter = additionalPendingFilter;
        }

        /// <summary>The post-delivery retention window stamped on a delivered document, in seconds.</summary>
        public int DeliveredTtlSeconds { get; }

        /// <summary>The Cosmos document-patch path for the delivery status field.</summary>
        public string StatusPatchPath { get; }

        /// <summary>The status value a delivered document is advanced to.</summary>
        public string DeliveredStatusValue { get; }

        /// <summary>
        /// The status value a PUBLISHED-BUT-UNCONFIRMED document is advanced to — one whose brokered message reached the
        /// broker but whose delivered stamp then failed. Read off <see cref="GiveUpPolicy"/>, so the value the relay
        /// STAMPS and the value the give-up log NAMES come from one derivation and cannot diverge.
        /// </summary>
        public string UnconfirmedStatusValue { get; }

        /// <summary>
        /// The VALIDATED give-up policy: both bounded outcomes (the opt-in pre-publish poison threshold and the always-on
        /// post-publish cap), the non-pending status each is stamped with, and the bounded per-document-identity failure
        /// counting that elects between them.
        /// </summary>
        public OutboxGiveUpPolicy GiveUpPolicy { get; }

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
        /// The settings reproducing the relay's original hard-coded drain knobs: a one-day delivered TTL, the
        /// <c>/status</c> -&gt; <c>delivered</c> stamp (the delivered TTL is always stamped at the hard-wired
        /// <c>/ttl</c> path), no additional pending filter (the always-applied
        /// <see cref="CosmosOutboxDocument.IsPendingOutbox"/> id-guard is the sole admission gate), and a DISABLED poison
        /// arm. It is NO LONGER byte-identical to the pre-seam relay in the POST-publish branch: it carries the always-on
        /// published-but-unconfirmed brake with its defaults, because that brake has no off switch, so a relay built from
        /// these settings stops re-publishing a document whose message went out but whose delivered stamp never landed.
        /// </summary>
        // INVARIANT: a NEW instance per access, never a shared singleton. The give-up policy it carries holds MUTABLE
        // per-identity failure streaks, so two relays sharing one instance would count each other's failures against
        // documents whose ids happened to collide.
        public static OutboxDeliverySettings Legacy => new OutboxDeliverySettings(
            deliveredTtlSeconds: 86400,
            statusPatchPath: "/" + CosmosOutboxDocument.StatusField,
            deliveredStatusValue: CosmosOutboxDocument.StatusDelivered,
            additionalPendingFilter: null);

        /// <summary>
        /// Maps a <see cref="CosmosOutboxRelayOptions"/>' three stamp knobs, its optional additional pending filter, and
        /// both give-up arms into the validating constructor. The single builder reused by the standalone host and
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
                options.PoisonStatusValue,
                options.GiveUpAfterUnconfirmedPublishes,
                options.UnconfirmedStatusValue);
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
