using System;
using System.Text.Json;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The configurable carrier for the #222 change-feed outbox relay's drain knobs: the pending-document predicate the
    /// relay filters on, and the field paths/values it stamps on a delivered document. <see cref="Legacy"/> reproduces
    /// the relay's original hard-coded behavior byte-for-byte, so a relay constructed without explicit settings is
    /// indistinguishable from the pre-seam relay.
    /// </summary>
    internal sealed class OutboxDeliverySettings
    {
        public OutboxDeliverySettings(int deliveredTtlSeconds,
                                      string statusPatchPath,
                                      string deliveredStatusValue,
                                      string ttlPatchPath,
                                      Func<JsonElement, bool> pendingFilter)
        {
            DeliveredTtlSeconds = deliveredTtlSeconds;
            StatusPatchPath = statusPatchPath ?? throw new ArgumentNullException(nameof(statusPatchPath));
            DeliveredStatusValue = deliveredStatusValue ?? throw new ArgumentNullException(nameof(deliveredStatusValue));
            TtlPatchPath = ttlPatchPath ?? throw new ArgumentNullException(nameof(ttlPatchPath));
            PendingFilter = pendingFilter ?? throw new ArgumentNullException(nameof(pendingFilter));
        }

        /// <summary>The post-delivery retention window stamped on a delivered document, in seconds.</summary>
        public int DeliveredTtlSeconds { get; }

        /// <summary>The Cosmos document-patch path for the delivery status field.</summary>
        public string StatusPatchPath { get; }

        /// <summary>The status value a delivered document is advanced to.</summary>
        public string DeliveredStatusValue { get; }

        /// <summary>The Cosmos document-patch path for the system TTL property.</summary>
        public string TtlPatchPath { get; }

        /// <summary>The predicate that admits a change-feed document as a pending outbox document to drain.</summary>
        public Func<JsonElement, bool> PendingFilter { get; }

        /// <summary>
        /// The settings reproducing the relay's original hard-coded behavior: a one-day delivered TTL, the
        /// <c>/status</c> -&gt; <c>delivered</c> stamp, the <c>/ttl</c> path, and the
        /// <see cref="CosmosOutboxDocument.IsPendingOutbox"/> pending predicate.
        /// </summary>
        public static OutboxDeliverySettings Legacy { get; } = new OutboxDeliverySettings(
            deliveredTtlSeconds: 86400,
            statusPatchPath: "/" + CosmosOutboxDocument.StatusField,
            deliveredStatusValue: CosmosOutboxDocument.StatusDelivered,
            ttlPatchPath: "/ttl",
            pendingFilter: CosmosOutboxDocument.IsPendingOutbox);
    }
}
