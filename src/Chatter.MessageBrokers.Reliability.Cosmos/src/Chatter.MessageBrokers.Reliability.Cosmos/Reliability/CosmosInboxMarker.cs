using Chatter.MessageBrokers;
using System;
using System.Collections.Generic;
using System.Linq;
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
        /// The optional two-phase completion-state field name (#253, ADR-0009 D1 amendment). Emitted ONLY when the caller
        /// opts in; the document-tier call site never opts in, so its marker render stays byte-identical.
        /// </summary>
        public const string CompletedField = nameof(Completed);

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

        /// <summary>
        /// The STANDALONE-inbox reserved root field names (#253), DERIVED BY CONSTRUCTION from the fields the standalone
        /// marker actually renders rather than enumerated: it reads the top-level field names off a representative render
        /// of <see cref="BuildBaseDocument"/> — the SAME field-name source <see cref="ToJsonObject"/> composes — so any
        /// FUTURE optional render field is reserved automatically (no per-field guard to add). This is a SUPERSET of
        /// <see cref="_reservedRootFields"/>: the standalone marker always opts into completion, so <see cref="CompletedField"/>
        /// is always reserved, and it stamps <see cref="CosmosOutboxDocument.TtlField"/> iff a positive
        /// <paramref name="markerTimeToLive"/> is configured, so <c>ttl</c> is reserved ONLY then. This set is scoped to the
        /// standalone inbox: the document tier renders neither <c>Completed</c> nor <c>ttl</c>, so its narrower
        /// <see cref="_reservedRootFields"/> guard (and its legal doc-tier <c>/ttl</c>/<c>/Completed</c> partition paths)
        /// is unaffected.
        /// </summary>
        public static IReadOnlyCollection<string> StandaloneReservedRootFields(int? markerTimeToLive)
        {
            // Render a representative standalone base document (no partition stamping) and read its top-level field names.
            // The standalone marker always renders Completed, and renders ttl iff a positive TTL is configured; the id,
            // message id and received-at values are irrelevant here — only the FIELD NAMES the render emits matter.
            CosmosInboxMarker representative = new CosmosInboxMarker(id: string.Empty, messageId: string.Empty, receivedAtUtc: default);
            JsonObject baseDocument = representative.BuildBaseDocument(effectiveTtlSeconds: markerTimeToLive, effectiveCompleted: false);
            return new HashSet<string>(baseDocument.Select(pair => pair.Key), StringComparer.Ordinal);
        }

        public CosmosInboxMarker(string id, string messageId, DateTime receivedAtUtc, int? ttlSeconds = null, bool? completed = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            MessageId = messageId;
            ReceivedAtUtc = receivedAtUtc;
            TtlSeconds = ttlSeconds;
            Completed = completed;
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
        /// The optional two-phase completion state (#253, ADR-0009 D1 amendment). Null means the <see cref="CompletedField"/>
        /// is NOT emitted (the document-tier call site leaves it unset, so its marker renders byte-identically); false
        /// stamps a PENDING claim; true stamps a COMPLETED marker. A standalone-inbox redelivery skips the handler ONLY on
        /// a COMPLETED marker (confirm-on-completion), so a persisted-but-abandoned pending marker is taken over rather
        /// than confirming a false duplicate.
        /// </summary>
        public bool? Completed { get; }

        /// <summary>
        /// Builds the inbox marker for <paramref name="messageId"/>: id <c>inbox:{encoded(messageId)}</c> via the shared
        /// Cosmos-id-safe encoder (inheriting its ≤1023-char id-length guard), the raw message id stored verbatim, and
        /// <see cref="ReceivedAtUtc"/> set to <see cref="DateTime.UtcNow"/>. The optional <paramref name="ttlSeconds"/>
        /// carries the dedup-window TTL (#253) onto <see cref="TtlSeconds"/>; the optional <paramref name="completed"/>
        /// carries the two-phase completion state (#253, ADR-0009 D1 amendment) onto <see cref="Completed"/>. The defaults
        /// (both null) keep the marker persistent with no completion field, so the document-tier call site — which passes
        /// neither — renders byte-identically.
        /// </summary>
        public static CosmosInboxMarker From(string messageId, int? ttlSeconds = null, bool? completed = null)
            => new CosmosInboxMarker(
                id: CosmosItemId.ForInbox(messageId),
                messageId: messageId,
                receivedAtUtc: DateTime.UtcNow,
                ttlSeconds: ttlSeconds,
                completed: completed);

        /// <summary>
        /// Composes the marker body, stamping each resolved partition-key value at its declared container path segment
        /// via the shared <see cref="CosmosPartitionKeyStamping"/> primitive with the inbox marker's own
        /// reserved-root-field set. <paramref name="partitionKeyPath"/> and <paramref name="partitionKeyValues"/> map
        /// positionally (a hierarchical path stamps one value per path). <see cref="ReceivedAtUtc"/> is serialized via
        /// <see cref="ChatterJson.Options"/> (ISO-8601). The optional <paramref name="ttlSeconds"/> is the render-time
        /// dedup-window TTL (#253): when a positive value is available — the explicit argument wins, else the marker's
        /// own <see cref="TtlSeconds"/> — the Cosmos-reserved <c>ttl</c> field is stamped so Cosmos self-purges the
        /// marker. A null/non-positive TTL emits NO <c>ttl</c> field. The optional <paramref name="completed"/> is the
        /// render-time two-phase completion state (#253, ADR-0009 D1 amendment): when a value is available — the explicit
        /// argument wins, else the marker's own <see cref="Completed"/> — the <see cref="CompletedField"/> is stamped;
        /// null emits NO completion field. Because the document-tier call site passes NEITHER a TTL nor a completion
        /// value, its marker renders byte-identically to the pre-amendment wire shape.
        /// </summary>
        public JsonObject ToJsonObject(IReadOnlyList<string> partitionKeyPath, IReadOnlyList<JsonElement> partitionKeyValues, int? ttlSeconds = null, bool? completed = null)
        {
            // The explicit argument wins, else the marker's own configured value.
            int? effectiveTtlSeconds = ttlSeconds ?? TtlSeconds;
            bool? effectiveCompleted = completed ?? Completed;

            JsonObject document = BuildBaseDocument(effectiveTtlSeconds, effectiveCompleted);

            CosmosPartitionKeyStamping.Stamp(document, partitionKeyPath, partitionKeyValues, _reservedRootFields);

            return document;
        }

        /// <summary>
        /// Composes the marker body's top-level fields WITHOUT partition-key stamping. This is the SINGLE field-name
        /// source shared by <see cref="ToJsonObject"/> (which stamps the partition value onto the returned object) and
        /// <see cref="StandaloneReservedRootFields"/> (which reads the emitted field names to derive the standalone
        /// reserved-root set by construction). Any optional field added here is therefore reserved automatically.
        /// </summary>
        private JsonObject BuildBaseDocument(int? effectiveTtlSeconds, bool? effectiveCompleted)
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
            // legitimate /ttl partition-key path on the document tier, which never emits a ttl. The standalone inbox DOES
            // emit a ttl, so its own registration-time guard (StandaloneReservedRootFields) reserves it fail-loud instead.
            if (effectiveTtlSeconds > 0)
            {
                document[CosmosOutboxDocument.TtlField] = effectiveTtlSeconds.Value;
            }

            // Optional two-phase completion state (#253, ADR-0009 D1 amendment): stamp the Completed field ONLY when a
            // value is opted into. CompletedField is deliberately NOT in _reservedRootFields (like ttl). A null value
            // emits NO field, so the document-tier call site — which opts into neither ttl nor completion — renders
            // byte-identically to the pre-amendment shape.
            if (effectiveCompleted.HasValue)
            {
                document[CompletedField] = effectiveCompleted.Value;
            }

            return document;
        }
    }
}
