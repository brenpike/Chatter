using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The testable core of the #222 document-tier change-feed outbox relay, isolated from the
    /// <see cref="ChangeFeedProcessor"/> plumbing that feeds it (that host lives in
    /// <see cref="CosmosOutboxRelayHostedService"/>). Given a single change-feed document — read as a
    /// <see cref="JsonElement"/> so Chatter owns the wire shape end-to-end (no Cosmos-SDK Newtonsoft serialization of
    /// the relay's reads) — it:
    /// <list type="number">
    /// <item>FILTERS to outbox documents only: the document's <c>_chatterType</c> discriminator must equal
    /// <see cref="CosmosItemId.OutboxKind"/> AND its <c>status</c> must equal <see cref="CosmosOutboxDocument.StatusPending"/>.
    /// A domain document, an inbox marker (<c>_chatterType="inbox"</c>), an already-delivered outbox document, or a
    /// malformed outbox document with a missing/empty status is NOT pending and is skipped — including the relay's OWN
    /// delivered/TTL update event (publish-once by construction).</item>
    /// <item>RECONSTRUCTS the <see cref="OutboundBrokeredMessage"/> from the persisted fields, mirroring
    /// <c>OutboxProcessor.Process</c> exactly (MessageId verbatim, Destination, MessageBody, MessageContentType,
    /// MessageContext materialized through <c>ChatterJson.Options</c> via <see cref="MessageContext.MaterializePersistedContext"/>,
    /// content-type fallback to the persisted MessageContext, infrastructure type read from the MessageContext).</item>
    /// <item>PUBLISHES via <c>IMessagingInfrastructureProvider.GetDispatcher(infra).Dispatch(message, null)</c> — the
    /// SAME no-reliability-re-entry path the EF relational relay uses (NOT <c>IBrokeredMessageDispatcher</c>, which can
    /// route back through the outbox and recurse).</item>
    /// <item>On publish success, STAMPS delivered + TTL: a SINGLE <see cref="Container.PatchItemAsync"/> with two ops —
    /// set <c>/status="delivered"</c> and set <c>/ttl=&lt;positive seconds&gt;</c> — keyed by the document id and the
    /// partition key recovered from the change-feed document at the container's declared partition-key path. Cosmos then
    /// self-purges the delivered document once its TTL elapses (the container MUST have <c>defaultTtl</c> enabled — an
    /// application prerequisite, not enforced here).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// AT-LEAST-ONCE. Publish and the delivered/TTL patch are two separate writes: a publish that succeeds and then a
    /// patch that fails leaves the document <c>pending</c>, so it re-surfaces on the next change-feed pass and is
    /// re-published — downstream consumers dedup via the #220 document-tier inbox marker. A publish that THROWS performs
    /// NO patch and PROPAGATES the exception out of <see cref="ProcessChangeAsync"/>; the host lets it escape the
    /// change-feed handler so the SDK does NOT checkpoint the batch (the document re-surfaces next pass) rather than
    /// advancing the lease past an unpublished document.
    /// </remarks>
    internal sealed class CosmosOutboxRelay
    {
        // The delivered-state status literal the relay advances a published outbox document to (sibling of
        // CosmosOutboxDocument.StatusPending). A document at this status is no longer pending, so the in-code filter
        // skips it — this is what suppresses the relay's own delivered/TTL update event (publish-once by construction).
        // Lives on CosmosOutboxDocument so the status vocabulary has one ground truth; referenced here by name.

        // The Cosmos document-patch path for the delivery status, derived from the wire field name so the patch targets
        // the SAME property the outbox document renders and the filter reads.
        private const string StatusPatchPath = "/" + CosmosOutboxDocument.StatusField;

        // The Cosmos system TTL property path. Cosmos honors a per-document "ttl" (seconds) ONLY when the container has
        // defaultTtl enabled (a documented application prerequisite); the relay stamps a positive value so a delivered
        // document self-purges after the retention window rather than accumulating forever.
        private const string TtlPatchPath = "/ttl";

        // The post-delivery retention window stamped on a delivered outbox document, in seconds. A short positive
        // retention (one day) lets the document linger briefly for operational inspection/debugging after publish, then
        // Cosmos self-purges it. The value only takes effect when the container has defaultTtl enabled.
        internal const int DeliveredTtlSeconds = 86400;

        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        private readonly IBodyConverterFactory _bodyConverterFactory;

        public CosmosOutboxRelay(IMessagingInfrastructureProvider infrastructureProvider, IBodyConverterFactory bodyConverterFactory)
        {
            _infrastructureProvider = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
        }

        /// <summary>
        /// Processes a single change-feed document against <paramref name="monitoredContainer"/> (the container the
        /// document lives in, used for the delivered/TTL patch). <paramref name="partitionKeyPath"/> is the container's
        /// declared partition-key path (single or hierarchical) — the document carries its partition-key value(s) at
        /// these segments, and the delivered/TTL patch must target the SAME logical partition. A non-outbox or
        /// non-pending document is a no-op. An outbox+pending document is reconstructed, published, then patched
        /// delivered+TTL. A publish failure performs no patch and propagates so the host does not checkpoint the
        /// change-feed batch.
        /// </summary>
        public async Task ProcessChangeAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken = default)
        {
            _ = monitoredContainer ?? throw new ArgumentNullException(nameof(monitoredContainer));
            _ = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));

            if (!IsPendingOutbox(document))
            {
                return;
            }

            OutboundBrokeredMessage outbound = Reconstruct(document);

            IDictionary<string, object> messageContext = outbound.MessageContext;
            messageContext.TryGetValue(MessageContext.InfrastructureType, out var infra);
            IMessagingInfrastructureDispatcher dispatcher = _infrastructureProvider.GetDispatcher((string)infra);

            // PUBLISH FIRST, PATCH SECOND. A throw here propagates with no patch issued — the document stays pending and
            // re-surfaces next change-feed pass (at-least-once); the host does not checkpoint the batch.
            await dispatcher.Dispatch(outbound, null);

            await StampDeliveredAsync(document, monitoredContainer, partitionKeyPath, cancellationToken);
        }

        // FILTER: an outbox document the relay must publish is exactly one whose discriminator equals the outbox kind AND
        // whose status equals "pending". A domain doc, an inbox marker, an already-delivered outbox doc (the relay's own
        // update event), or a malformed outbox doc with a missing/non-string/empty status is NOT pending -> skipped.
        private static bool IsPendingOutbox(JsonElement document)
        {
            if (document.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(document, CosmosOutboxDocument.DiscriminatorField, out string discriminator)
                || !string.Equals(discriminator, CosmosItemId.OutboxKind, StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryGetString(document, CosmosOutboxDocument.StatusField, out string status)
                || string.IsNullOrWhiteSpace(status)
                || !string.Equals(status, CosmosOutboxDocument.StatusPending, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        // RECONSTRUCT the OutboundBrokeredMessage from the persisted outbox fields, mirroring OutboxProcessor.Process:
        // MessageId verbatim, Destination, MessageBody, MessageContentType, MessageContext materialized through
        // ChatterJson.Options; content-type falls back to the persisted MessageContext when the doc content-type is
        // empty; the body bytes come from an IBodyConverterFactory converter for the resolved content type.
        private OutboundBrokeredMessage Reconstruct(JsonElement document)
        {
            TryGetString(document, CosmosOutboxDocument.MessageIdField, out string messageId);
            TryGetString(document, CosmosOutboxDocument.DestinationField, out string destination);
            TryGetString(document, CosmosOutboxDocument.MessageBodyField, out string messageBody);
            TryGetString(document, CosmosOutboxDocument.MessageContentTypeField, out string messageContentType);
            TryGetString(document, CosmosOutboxDocument.MessageContextField, out string serializedMessageContext);

            // MaterializePersistedContext deserializes the persisted MessageContext JSON string through
            // ChatterJson.Options, restoring the CLR types the typed (string)/(DateTime?)/integer reads downstream
            // depend on (parity with OutboxProcessor.Process).
            IDictionary<string, object> messageContext = MessageContext.MaterializePersistedContext(serializedMessageContext);

            string contentType = messageContentType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = messageContext.TryGetValue(MessageContext.ContentType, out var ct) ? (string)ct : null;
            }

            if (string.IsNullOrWhiteSpace(contentType))
            {
                throw new InvalidOperationException(
                    $"Outbox document '{messageId}' has no content type in the document or its message context; a content type is required to serialize and publish the brokered message.");
            }

            IBrokeredMessageBodyConverter converter = _bodyConverterFactory.CreateBodyConverter(contentType);

            return new OutboundBrokeredMessage(messageId, converter.GetBytes(messageBody), messageContext, destination, converter);
        }

        // POST-PUBLISH: a SINGLE PatchItemAsync with two ops (set /status="delivered", set /ttl=<positive seconds>),
        // keyed by the document id read off the change-feed item and the partition key recovered from the same item at
        // the container's declared partition-key path. PatchItem (not ReplaceItem) so only the two delivery fields are
        // touched and the aggregate-shaped wire body is left untouched.
        private static async Task StampDeliveredAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken)
        {
            if (!TryGetString(document, CosmosOutboxDocument.IdField, out string id) || string.IsNullOrEmpty(id))
            {
                throw new InvalidOperationException("A pending outbox document is missing its 'id'; cannot stamp it delivered.");
            }

            PartitionKey partitionKey = RecoverPartitionKey(document, partitionKeyPath);

            var patchOperations = new List<PatchOperation>
            {
                PatchOperation.Set(StatusPatchPath, CosmosOutboxDocument.StatusDelivered),
                PatchOperation.Set(TtlPatchPath, DeliveredTtlSeconds),
            };

            await monitoredContainer.PatchItemAsync<JsonElement>(id, partitionKey, patchOperations, requestOptions: null, cancellationToken: cancellationToken);
        }

        // Recovers the document's partition key by reading the value(s) the document carries at the container's declared
        // partition-key path (single or hierarchical) and building a PartitionKey that preserves each value's JSON kind
        // (string/number/bool/null), so the delivered/TTL patch lands in the SAME logical partition the document lives
        // in. A path segment may be nested (e.g. "/tenant/id"), navigated object-by-object; a missing/array-valued
        // intermediate yields a JSON-null component, mirroring how the document was stamped on write.
        private static PartitionKey RecoverPartitionKey(JsonElement document, IReadOnlyList<string> partitionKeyPath)
        {
            var builder = new PartitionKeyBuilder();
            foreach (string path in partitionKeyPath)
            {
                JsonElement component = NavigateToPathValue(document, path);
                switch (component.ValueKind)
                {
                    case JsonValueKind.String:
                        builder.Add(component.GetString());
                        break;
                    case JsonValueKind.Number:
                        builder.Add(component.GetDouble());
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        builder.Add(component.GetBoolean());
                        break;
                    default:
                        builder.AddNullValue();
                        break;
                }
            }

            return builder.Build();
        }

        // Navigates the document to the value at a partition-key path segment (e.g. "/tenant/id"), descending one nested
        // object per path part. Returns an undefined JsonElement (ValueKind == Undefined) when any part is absent or a
        // non-object intermediate is hit, which RecoverPartitionKey maps to a JSON-null partition-key component.
        private static JsonElement NavigateToPathValue(JsonElement document, string path)
        {
            JsonElement current = document;
            foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out JsonElement next))
                {
                    return default;
                }

                current = next;
            }

            return current;
        }

        private static bool TryGetString(JsonElement document, string propertyName, out string value)
        {
            if (document.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
                return true;
            }

            value = null;
            return false;
        }
    }
}
