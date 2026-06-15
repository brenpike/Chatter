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
            if (partitionKeyPath is null || partitionKeyPath.Count == 0)
            {
                throw new ArgumentException("A container partition-key path is required to stamp the outbox document.", nameof(partitionKeyPath));
            }

            _ = partitionKeyValues ?? throw new ArgumentNullException(nameof(partitionKeyValues));
            if (partitionKeyValues.Count != partitionKeyPath.Count)
            {
                throw new ArgumentException(
                    $"Expected '{partitionKeyPath.Count}' partition-key value(s) to match the path segment count but got '{partitionKeyValues.Count}'.",
                    nameof(partitionKeyValues));
            }

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

            for (var i = 0; i < partitionKeyPath.Count; i++)
            {
                StampPartitionKeySegment(document, partitionKeyPath[i], partitionKeyValues[i]);
            }

            return document;
        }

        // Stamps a single partition-key path (e.g. "/tenant/id") into the document, creating intermediate objects for
        // each non-leaf segment so the value lands at the real container path rather than a flattened fixed field. A
        // fresh JsonNode is minted from the JsonElement on every call so this document never receives a node that is
        // already parented to another document — cross-document reparenting is structurally impossible.
        private static void StampPartitionKeySegment(JsonObject root, string partitionKeyPath, JsonElement partitionKeyValue)
        {
            var segments = partitionKeyPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                throw new ArgumentException("A partition-key path segment must contain at least one property name.", nameof(partitionKeyPath));
            }

            // COLLISION GUARD: a partition-key path whose ROOT segment is a Chatter-reserved field would overwrite a
            // required Chatter value (e.g. /id replaces the deterministic outbox id, colliding every doc in the
            // partition; /MessageContext/x replaces the serialized context string with an object). Fail loudly rather
            // than silently corrupt the document. This eliminates the reserved-field-overwrite class by construction.
            if (_reservedRootFields.Contains(segments[0]))
            {
                throw new InvalidOperationException(
                    $"The container partition-key path '{partitionKeyPath}' targets the Chatter-reserved outbox field '{segments[0]}'. " +
                    "Co-resident outbox documents cannot be stamped on a partition-key path whose root segment is one of " +
                    $"[{string.Join(", ", _reservedRootFields)}] without overwriting a required field. Use a non-reserved partition-key path for the container.");
            }

            JsonObject current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                if (current[segment] is JsonObject existing)
                {
                    current = existing;
                }
                else
                {
                    var nested = new JsonObject();
                    current[segment] = nested;
                    current = nested;
                }
            }

            // Mint a fresh JsonNode from the detached JsonElement so this leaf has no prior parent and each document
            // gets its own independent node. JsonNode.Parse returns null for a JSON null literal, which is the correct
            // JSON-null leaf for a null partition-key component.
            current[segments[^1]] = JsonNode.Parse(partitionKeyValue.GetRawText());
        }
    }
}
