using Chatter.MessageBrokers;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The Chatter-owned co-resident inbox dedup marker (#220, ADR-0007). It rides the framework-owned
    /// <c>TransactionalBatch</c> alongside the application's aggregate upsert and the outbox doc so all three commit
    /// atomically; a 409-on-create of this marker at batch-execute time is a confirmed duplicate (the message has
    /// already been handled in this partition), so the whole batch fails atomically and the message is acked without
    /// re-handling. Dedup is closed-by-construction via the create-conflict — there is no read-then-add (no TOCTOU).
    /// The marker carries the <c>_chatterType="inbox"</c> discriminator so the change-feed relay (#222, which drains
    /// the <c>outbox</c> discriminator) ignores markers by construction. There is intentionally NO status field. The
    /// dedup-window TTL is OPTIONAL (#253): when a positive <see cref="TtlSeconds"/> is configured the marker stamps the
    /// Cosmos-reserved <c>ttl</c> field so Cosmos self-purges it after the window; left unset (the document-tier path)
    /// the marker persists indefinitely and renders byte-identically to a marker with no TTL support at all.
    /// </summary>
    /// <remarks>
    /// The document body is produced as a System.Text.Json object so Chatter owns the wire shape end-to-end (the Cosmos
    /// SDK's own serializer never sees it; the inbox marker stages a <c>CreateItemStream</c> op of the bytes this type
    /// renders), mirroring <see cref="CosmosOutboxDocument"/>. The resolved partition-key value is stamped at the
    /// container's ACTUAL partition-key path (supporting a hierarchical path) via the shared
    /// <see cref="CosmosPartitionKeyStamping"/> primitive, NOT at a fixed field named <c>partitionKey</c>.
    /// </remarks>
    public sealed class CosmosInboxMarker
    {
        /// <summary>The Chatter-reserved discriminator field name (shared with the outbox doc; namespaced so it cannot collide with an app field).</summary>
        public const string DiscriminatorField = CosmosOutboxDocument.DiscriminatorField;

        /// <summary>The document-id field name (Cosmos requires the item id under the reserved <c>id</c> property).</summary>
        public const string IdField = CosmosOutboxDocument.IdField;

        /// <summary>The verbatim message-id field name.</summary>
        public const string MessageIdField = nameof(MessageId);

        /// <summary>The received-at-UTC field name.</summary>
        public const string ReceivedAtUtcField = nameof(ReceivedAtUtc);

        /// <summary>
        /// The Chatter-reserved root field names for the inbox marker. A container partition-key path whose ROOT segment
        /// matches any of these would overwrite a required marker field (most damaging: <c>/id</c> would replace the
        /// deterministic <c>inbox:{encoded(MessageId)}</c> item id with the partition value, defeating the dedup). The
        /// shared stamping primitive validates the root segment against this set and fails loudly rather than silently
        /// corrupting the document — the collision class is eliminated by construction, not enumerated.
        /// </summary>
        private static readonly HashSet<string> _reservedRootFields = new HashSet<string>(StringComparer.Ordinal)
        {
            IdField,
            DiscriminatorField,
            MessageIdField,
            ReceivedAtUtcField,
        };

        /// <summary>The Chatter-reserved root field names (read-only view of the collision-guard set).</summary>
        public static IReadOnlyCollection<string> ReservedRootFields => _reservedRootFields;

        public CosmosInboxMarker(string id, string messageId, DateTime receivedAtUtc, int? ttlSeconds = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            MessageId = messageId;
            ReceivedAtUtc = receivedAtUtc;
            TtlSeconds = ttlSeconds;
        }

        /// <summary>The physical Cosmos item id, <c>inbox:{encoded(MessageId)}</c>.</summary>
        public string Id { get; }

        /// <summary>The raw inbound message id, stored verbatim as the dedup identity.</summary>
        public string MessageId { get; }

        /// <summary>The UTC instant the marker was stamped (the message was first handled in this partition).</summary>
        public DateTime ReceivedAtUtc { get; }

        /// <summary>
        /// The optional dedup-window time-to-live, in seconds. When positive it is stamped at the Cosmos-reserved
        /// <c>ttl</c> field so Cosmos self-purges the marker after the window; null or non-positive means the marker
        /// persists indefinitely and NO <c>ttl</c> field is emitted (the document-tier path leaves this unset).
        /// </summary>
        public int? TtlSeconds { get; }

        /// <summary>
        /// Builds the inbox marker for <paramref name="messageId"/>: id <c>inbox:{encoded(messageId)}</c> via the shared
        /// Cosmos-id-safe encoder (inheriting its ≤1023-char id-length guard), the raw message id stored verbatim, and
        /// <see cref="ReceivedAtUtc"/> set to <see cref="DateTime.UtcNow"/>. The optional <paramref name="ttlSeconds"/>
        /// carries the dedup-window TTL (#253) onto <see cref="TtlSeconds"/>; the default null keeps the marker
        /// persistent (the document-tier call site passes none, rendering byte-identically).
        /// </summary>
        public static CosmosInboxMarker From(string messageId, int? ttlSeconds = null)
            => new CosmosInboxMarker(
                id: CosmosItemId.ForInbox(messageId),
                messageId: messageId,
                receivedAtUtc: DateTime.UtcNow,
                ttlSeconds: ttlSeconds);

        /// <summary>
        /// Composes the marker body, stamping each resolved partition-key value at its declared container path segment
        /// via the shared <see cref="CosmosPartitionKeyStamping"/> primitive with the inbox marker's own
        /// reserved-root-field set. <paramref name="partitionKeyPath"/> and <paramref name="partitionKeyValues"/> map
        /// positionally (a hierarchical path stamps one value per path). <see cref="ReceivedAtUtc"/> is serialized via
        /// <see cref="ChatterJson.Options"/> (ISO-8601). The optional <paramref name="ttlSeconds"/> is the render-time
        /// dedup-window TTL (#253): when a positive value is available — the explicit argument wins, else the marker's
        /// own <see cref="TtlSeconds"/> — the Cosmos-reserved <c>ttl</c> field is stamped so Cosmos self-purges the
        /// marker. A null/non-positive TTL emits NO <c>ttl</c> field, so the document-tier call site (which passes
        /// neither) renders byte-identically to the pre-TTL wire shape.
        /// </summary>
        public JsonObject ToJsonObject(IReadOnlyList<string> partitionKeyPath, IReadOnlyList<JsonElement> partitionKeyValues, int? ttlSeconds = null)
        {
            var document = new JsonObject
            {
                [IdField] = Id,
                [DiscriminatorField] = CosmosItemId.InboxKind,
                [MessageIdField] = MessageId,
                [ReceivedAtUtcField] = JsonNode.Parse(JsonSerializer.Serialize(ReceivedAtUtc, ChatterJson.Options)),
            };

            // Optional dedup-window TTL (#253, ADR-0009 D3): stamp the Cosmos-reserved ttl field ONLY when a positive
            // ttl is available. "ttl" is deliberately NOT in _reservedRootFields — adding it would newly reject a
            // legitimate /ttl partition-key path on the document tier — so the collision guard does not cover it; the
            // standalone inbox always partitions on a non-ttl path (/idempotencyKey).
            int? effectiveTtlSeconds = ttlSeconds ?? TtlSeconds;
            if (effectiveTtlSeconds > 0)
            {
                document[CosmosOutboxDocument.TtlField] = effectiveTtlSeconds.Value;
            }

            CosmosPartitionKeyStamping.Stamp(document, partitionKeyPath, partitionKeyValues, _reservedRootFields);

            return document;
        }
    }
}
