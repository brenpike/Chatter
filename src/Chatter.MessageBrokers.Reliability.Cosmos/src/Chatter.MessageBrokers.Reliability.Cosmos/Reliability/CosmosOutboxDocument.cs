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
    }
}
