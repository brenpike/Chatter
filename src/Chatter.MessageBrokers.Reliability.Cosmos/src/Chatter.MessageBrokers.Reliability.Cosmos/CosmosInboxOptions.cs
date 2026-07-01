using System;
using System.Collections.Generic;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Configures the STANDALONE, lease-less Cosmos inbox-dedup gate registered via
    /// <see cref="CosmosInboxServiceCollectionExtensions.WithCosmosInbox"/> (#253, ADR-0009). Unlike the document tier
    /// (<c>WithCosmosDocumentReliability&lt;TCommand&gt;</c>, which dedups inside the aggregate's
    /// <c>TransactionalBatch</c>), the standalone inbox performs an anti-TOCTOU write-ahead claim — a
    /// <c>CreateItemStream</c> of a <see cref="Chatter.MessageBrokers.Reliability.Cosmos.CosmosInboxMarker"/> on an
    /// <c>/idempotencyKey</c>-partitioned container — through the existing <c>InboxBehavior&lt;T&gt;</c> seam, skipping
    /// the handler on a CONFIRMED duplicate. It registers NO lease container, NO relay host, NO outbox, and NO router
    /// replacement; the application owns the container and the <see cref="Microsoft.Azure.Cosmos.CosmosClient"/> lifecycle.
    /// </summary>
    public sealed class CosmosInboxOptions
    {
        /// <summary>REQUIRED. The Cosmos database the idempotency (marker) container lives in.</summary>
        public string Database { get; set; }

        /// <summary>REQUIRED. The idempotency container the inbox stamps its dedup markers into.</summary>
        public string Container { get; set; }

        /// <summary>
        /// The idempotency container's partition-key path. Defaults to <c>["/idempotencyKey"]</c>. v1 supports only a
        /// SINGLE-segment path (the partition value is the inbound message id); hierarchical support is deferred to
        /// backlog #254, so <see cref="CosmosInboxServiceCollectionExtensions.WithCosmosInbox"/> rejects a multi-segment path.
        /// </summary>
        public IReadOnlyList<string> PartitionKeyPath { get; set; } = new[] { "/idempotencyKey" };

        /// <summary>
        /// Optional dedup-window TTL, in seconds, stamped onto each marker so Cosmos self-purges it after the window.
        /// Defaults to null (markers persist indefinitely). A non-positive value emits no TTL (byte-identical to unset).
        /// </summary>
        public int? MarkerTimeToLive { get; set; } = null;

        /// <summary>
        /// The maximum number of point-read attempts when CONFIRMING a create-409 whose conflicting marker is not yet
        /// visible under session consistency (ADR-0009 D1). Defaults to 5. Must be at least 1.
        /// </summary>
        public int ReadBackMaxAttempts { get; set; } = 5;

        /// <summary>
        /// The backoff between confirm read-back attempts. Defaults to 50ms. Must be non-negative.
        /// </summary>
        public TimeSpan ReadBackInterval { get; set; } = TimeSpan.FromMilliseconds(50);
    }
}
