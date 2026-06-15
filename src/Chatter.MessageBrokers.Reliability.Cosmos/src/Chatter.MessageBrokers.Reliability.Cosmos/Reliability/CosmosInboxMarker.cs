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
    /// the <c>outbox</c> discriminator) ignores markers by construction. There is intentionally NO status field and NO
    /// TTL — the marker persists for the dedup window (an app-configurable dedup-window TTL is a deferred design point).
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

        public CosmosInboxMarker(string id, string messageId, DateTime receivedAtUtc)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            MessageId = messageId;
            ReceivedAtUtc = receivedAtUtc;
        }

        /// <summary>The physical Cosmos item id, <c>inbox:{encoded(MessageId)}</c>.</summary>
        public string Id { get; }

        /// <summary>The raw inbound message id, stored verbatim as the dedup identity.</summary>
        public string MessageId { get; }

        /// <summary>The UTC instant the marker was stamped (the message was first handled in this partition).</summary>
        public DateTime ReceivedAtUtc { get; }

        /// <summary>
        /// Builds the inbox marker for <paramref name="messageId"/>: id <c>inbox:{encoded(messageId)}</c> via the shared
        /// Cosmos-id-safe encoder (inheriting its ≤1023-char id-length guard), the raw message id stored verbatim, and
        /// <see cref="ReceivedAtUtc"/> set to <see cref="DateTime.UtcNow"/>.
        /// </summary>
        public static CosmosInboxMarker From(string messageId)
            => new CosmosInboxMarker(
                id: CosmosItemId.ForInbox(messageId),
                messageId: messageId,
                receivedAtUtc: DateTime.UtcNow);

        /// <summary>
        /// Composes the marker body, stamping each resolved partition-key value at its declared container path segment
        /// via the shared <see cref="CosmosPartitionKeyStamping"/> primitive with the inbox marker's own
        /// reserved-root-field set. <paramref name="partitionKeyPath"/> and <paramref name="partitionKeyValues"/> map
        /// positionally (a hierarchical path stamps one value per path). <see cref="ReceivedAtUtc"/> is serialized via
        /// <see cref="ChatterJson.Options"/> (ISO-8601).
        /// </summary>
        public JsonObject ToJsonObject(IReadOnlyList<string> partitionKeyPath, IReadOnlyList<JsonElement> partitionKeyValues)
        {
            var document = new JsonObject
            {
                [IdField] = Id,
                [DiscriminatorField] = CosmosItemId.InboxKind,
                [MessageIdField] = MessageId,
                [ReceivedAtUtcField] = JsonNode.Parse(JsonSerializer.Serialize(ReceivedAtUtc, ChatterJson.Options)),
            };

            CosmosPartitionKeyStamping.Stamp(document, partitionKeyPath, partitionKeyValues, _reservedRootFields);

            return document;
        }
    }
}
