using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The Chatter-owned co-resident outbox document (ADR-0007). It rides the framework-owned
    /// <c>TransactionalBatch</c> alongside the application's aggregate upsert so the two commit atomically; the
    /// change-feed relay (#222) drains it filtered by the <c>_chatterType="outbox"</c> discriminator and publishes only
    /// <see cref="StatusPending"/> documents.
    /// </summary>
    /// <remarks>
    /// The document body is produced as a System.Text.Json object so Chatter owns the wire shape end-to-end (the Cosmos
    /// SDK's own serializer — Newtonsoft by default — never sees it; the outbox stages a <c>CreateItemStream</c> op of
    /// the bytes this type renders). The resolved partition-key value is stamped at the container's ACTUAL partition-key
    /// path (supporting a hierarchical path), NOT at a fixed field named <c>partitionKey</c>.
    /// </remarks>
    public sealed class CosmosOutboxDocument
    {
        /// <summary>The Chatter-reserved discriminator field name (namespaced so it cannot collide with an app field).</summary>
        public const string DiscriminatorField = "_chatterType";

        /// <summary>The delivery-state field name; the #222 relay filters on it to publish only pending documents.</summary>
        public const string StatusField = "status";

        /// <summary>Initial delivery state; the relay advances it to delivered after publish.</summary>
        public const string StatusPending = "pending";

        /// <summary>
        /// Terminal delivery state the #222 relay advances a document to AFTER a successful publish (set together with a
        /// positive per-document TTL in a single patch). A document at this status is no longer
        /// <see cref="StatusPending"/>, so the relay's in-code change-feed filter skips it — this is what makes the
        /// relay's OWN delivered/TTL update event a non-republish (publish-once by construction).
        /// </summary>
        public const string StatusDelivered = "delivered";

        /// <summary>
        /// Non-pending delivery state for a document whose publish SUCCEEDED but whose delivered stamp could not be
        /// confirmed. It is deliberately distinct from <see cref="StatusDelivered"/> — a delivery Chatter watched land
        /// and a delivery it merely believes landed must stay tellable apart when an operator inspects the container —
        /// and, like <see cref="StatusDelivered"/>, it is not <see cref="StatusPending"/>, so the relay's admission gate
        /// stops admitting the document and cannot publish it again.
        /// </summary>
        public const string StatusUnconfirmed = "published-unconfirmed";

        /// <summary>
        /// The Cosmos system TTL field name. The #222 relay stamps a positive per-document TTL here on delivery so Cosmos
        /// self-purges the delivered document — this is the ONLY field Cosmos honors for self-purge, so the delivered
        /// stamp's ttl path is hard-wired here rather than configurable.
        /// </summary>
        public const string TtlField = "ttl";

        /// <summary>
        /// The Cosmos system timestamp field name, carrying the document's last-write time in Unix epoch SECONDS.
        /// Cosmos stamps it server-side, so Chatter never writes it; the #222 relay READS it to report how long an
        /// Outbox Document had been pending when it admitted it. It is deliberately absent from the reserved root
        /// fields, which guard only the fields Chatter itself stamps.
        /// </summary>
        public const string TimestampField = "_ts";

        /// <summary>The document-id field name (Cosmos requires the item id under the reserved <c>id</c> property).</summary>
        public const string IdField = "id";

        /// <summary>The verbatim message-id field name.</summary>
        public const string MessageIdField = nameof(MessageId);

        /// <summary>The destination field name.</summary>
        public const string DestinationField = nameof(Destination);

        /// <summary>The serialized-body field name.</summary>
        public const string MessageBodyField = nameof(MessageBody);

        /// <summary>The content-type field name.</summary>
        public const string MessageContentTypeField = nameof(MessageContentType);

        /// <summary>The serialized message-context field name.</summary>
        public const string MessageContextField = "MessageContext";

        /// <summary>
        /// The Chatter-reserved root field names. A container partition-key path whose ROOT segment matches any of these
        /// would overwrite a required Chatter field (most damaging: <c>/id</c> would replace the deterministic
        /// <c>outbox:{encoded(MessageId)}</c> item id with the partition value, colliding every outbox doc in the
        /// partition). Stamping validates the root segment against this set and fails loudly rather than silently
        /// corrupting the document — the collision class is eliminated by construction, not enumerated.
        /// </summary>
        private static readonly HashSet<string> _reservedRootFields = new HashSet<string>(StringComparer.Ordinal)
        {
            IdField,
            DiscriminatorField,
            StatusField,
            MessageIdField,
            DestinationField,
            MessageBodyField,
            MessageContentTypeField,
            MessageContextField,
        };

        /// <summary>The Chatter-reserved root field names (read-only view of the collision-guard set).</summary>
        public static IReadOnlyCollection<string> ReservedRootFields => _reservedRootFields;

        /// <summary>
        /// The document paths the #222 relay's OWN patch operations target when it stamps a drained document. Cosmos
        /// REJECTS a patch of the partition key, so a container partitioned on any of these can never be stamped: every
        /// document would publish, fail its stamp, stay pending, and re-publish on the next change-feed pass forever.
        /// <see cref="MonitoredContainerContract"/> rejects such a container at host start. The collection is DERIVED
        /// from the same field constants the patch ops are built from so the check can never drift from the ops.
        /// </summary>
        private static readonly IReadOnlyCollection<string> _relayStampedPaths = Array.AsReadOnly(new[]
        {
            "/" + StatusField,
            "/" + TtlField,
        });

        /// <summary>The paths the relay patches on every drained document (read-only view of the stamped-path set).</summary>
        public static IReadOnlyCollection<string> RelayStampedPaths => _relayStampedPaths;

        public CosmosOutboxDocument(string id, string messageId, string destination, string messageBody, string messageContentType, string serializedMessageContext)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            MessageId = messageId;
            Destination = destination;
            MessageBody = messageBody;
            MessageContentType = messageContentType;
            SerializedMessageContext = serializedMessageContext;
        }

        /// <summary>The physical Cosmos item id, <c>outbox:{encoded(MessageId)}</c>.</summary>
        public string Id { get; }

        /// <summary>The raw <see cref="OutboundBrokeredMessage.MessageId"/>, stored verbatim as the event id.</summary>
        public string MessageId { get; }

        public string Destination { get; }

        public string MessageBody { get; }

        public string MessageContentType { get; }

        /// <summary>The <see cref="OutboundBrokeredMessage.MessageContext"/> serialized with <see cref="ChatterJson.Options"/> (EF parity).</summary>
        public string SerializedMessageContext { get; }

        /// <summary>
        /// Builds the Chatter-owned outbox <see cref="OutboundBrokeredMessage"/> document for <paramref name="message"/>:
        /// id <c>outbox:{encoded(MessageId)}</c>, raw message id stored verbatim, message context serialized with
        /// <see cref="ChatterJson.Options"/> to match the EF provider.
        /// </summary>
        public static CosmosOutboxDocument From(OutboundBrokeredMessage message)
        {
            _ = message ?? throw new ArgumentNullException(nameof(message));

            return new CosmosOutboxDocument(
                id: CosmosItemId.ForOutbox(message.MessageId),
                messageId: message.MessageId,
                destination: message.Destination,
                messageBody: message.Stringify(),
                messageContentType: message.ContentType,
                serializedMessageContext: JsonSerializer.Serialize(message.MessageContext, ChatterJson.Options));
        }

        /// <summary>
        /// Composes the document body, stamping each resolved partition-key value at its declared container path
        /// segment. <paramref name="partitionKeyPath"/> and <paramref name="partitionKeyValues"/> map positionally: a
        /// hierarchical path (e.g. <c>["/tenant/id", "/region"]</c>) stamps one value per path, nesting intermediate
        /// objects so each value lands at its real container path rather than a flattened fixed field. Each call mints
        /// a fresh <see cref="JsonNode"/> from the caller-supplied <see cref="JsonElement"/> values so multiple
        /// documents may be built from the same value set without cross-document node re-parenting.
        /// </summary>
        // INVARIANT: the partition-key value is placed at the container's REAL declared path, never at a fixed field
        // named "partitionKey"; a hierarchical container stamps one leaf per path segment. The stamped value preserves
        // its JSON value kind (string/number/bool/null) so the document lands in the SAME logical partition the batch
        // was opened on — a non-string partition value must NOT be coerced to a JSON string.
        public JsonObject ToJsonObject(IReadOnlyList<string> partitionKeyPath, IReadOnlyList<JsonElement> partitionKeyValues)
        {
            var document = new JsonObject
            {
                [IdField] = Id,
                [DiscriminatorField] = CosmosItemId.OutboxKind,
                [StatusField] = StatusPending,
                [MessageIdField] = MessageId,
                [DestinationField] = Destination,
                [MessageBodyField] = MessageBody,
                [MessageContentTypeField] = MessageContentType,
                [MessageContextField] = SerializedMessageContext,
            };

            // Delegates to the shared stamping primitive, passing the outbox document's OWN reserved-root-field set so
            // a partition-key path that would overwrite a required outbox field fails loudly. Wire shape and stamping
            // behavior are unchanged from the prior private implementation.
            CosmosPartitionKeyStamping.Stamp(document, partitionKeyPath, partitionKeyValues, _reservedRootFields);

            return document;
        }

        /// <summary>
        /// The #222 change-feed filter: <paramref name="document"/> is a pending Chatter outbox document the relay must
        /// publish exactly when its <c>_chatterType</c> discriminator equals <see cref="CosmosItemId.OutboxKind"/>, AND
        /// its <c>status</c> equals <see cref="StatusPending"/>, AND its physical <c>id</c> is the deterministic outbox id
        /// Chatter mints for its verbatim <c>MessageId</c> (<c>id == CosmosItemId.ForOutbox(MessageId)</c>). A domain
        /// document, an inbox marker, an already-delivered outbox document (the relay's own delivered/TTL update event),
        /// or a malformed outbox document with a missing/non-string/empty status is NOT pending.
        /// </summary>
        // The id-consistency check is the publish-side analogue of the inbox-side confirmation ADR-0007 already chose (the
        // marker-409 branch confirms _chatterType=="inbox" AND MessageId match rather than inferring from the bare
        // discriminator/namespace). The application OWNS the container and can author a document carrying
        // _chatterType="outbox" + status="pending" through a raw Cosmos write that no staging guard closes; without this
        // check the relay would publish that app/domain document as a broker message and then patch it status=delivered +
        // ttl — a forbidden domain-document leak AND a mutation of app data. Requiring id == ForOutbox(MessageId) makes a
        // document the relay drains provably one Chatter itself minted (the id is a deterministic function of the verbatim
        // MessageId), closing that leak/mutation class by construction rather than enumerating doc shapes. A genuine
        // Chatter outbox doc always satisfies this because CosmosOutboxDocument stamps id = ForOutbox(MessageId) and
        // MessageId verbatim from the same message.
        public static bool IsPendingOutbox(JsonElement document)
        {
            if (document.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(document, DiscriminatorField, out string discriminator)
                || !string.Equals(discriminator, CosmosItemId.OutboxKind, StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetString(document, StatusField, out string status)
                || string.IsNullOrWhiteSpace(status)
                || !string.Equals(status, StatusPending, StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetString(document, IdField, out string id)
                || !TryGetString(document, MessageIdField, out string messageId)
                || string.IsNullOrEmpty(messageId)
                || !string.Equals(id, ExpectedOutboxId(messageId), StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        // The deterministic outbox id Chatter mints for a verbatim MessageId, recomputed from the persisted MessageId so
        // the filter can prove a drained document is one Chatter itself authored. ForOutbox can throw (e.g. a MessageId
        // that encodes past Cosmos's id-length limit) only for ids Chatter could never have written, so any throw here
        // means "not a genuine Chatter outbox doc" -> treat as non-pending and skip rather than fault the whole batch.
        private static string ExpectedOutboxId(string messageId)
        {
            try
            {
                return CosmosItemId.ForOutbox(messageId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Reads a string-valued property off a change-feed document, returning false (with a null out value) when the
        // property is absent or not a JSON string. Shared by the pending-outbox filter and the relay's field reads so the
        // wire shape is interpreted one way.
        internal static bool TryGetString(JsonElement document, string propertyName, out string value)
        {
            if (document.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
                return true;
            }

            value = null;
            return false;
        }

        // Reads an integral-number-valued property off a change-feed document, returning false (with a zero out value)
        // when the property is absent, not a JSON number, or not representable as a 64-bit integer. Sibling to
        // TryGetString so the wire shape stays interpreted in one place; the relay reads the Cosmos-stamped
        // TimestampField through it, and a document that never went through Cosmos simply carries none.
        internal static bool TryGetInt64(JsonElement document, string propertyName, out long value)
        {
            if (document.TryGetProperty(propertyName, out JsonElement element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt64(out value))
            {
                return true;
            }

            value = 0;
            return false;
        }
    }
}
