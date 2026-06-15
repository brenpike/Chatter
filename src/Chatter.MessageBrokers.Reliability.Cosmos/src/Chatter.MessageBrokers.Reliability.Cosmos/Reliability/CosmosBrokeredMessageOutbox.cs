using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The document-tier outbox (#219): the Cosmos realization of <see cref="IBrokeredMessageOutbox.SendToOutbox"/>.
    /// It resolves the active document-tier atomic-write handle from the reliability surface (surface-ownership — NOT
    /// from <see cref="TransactionContext.Container"/>), builds the Chatter-owned outbox document, and CONTRIBUTES its
    /// create-op to the framework-owned <see cref="TransactionalBatch"/> so it commits atomically with the handler's
    /// aggregate upsert. It never executes the batch — the Document-Tier Batch-Lifecycle Behavior owns the single
    /// commit point (framework-owns-batch-lifecycle).
    /// </summary>
    public sealed class CosmosBrokeredMessageOutbox : IBrokeredMessageOutbox
    {
        private readonly IDocumentTierReliabilitySurface _surface;

        public CosmosBrokeredMessageOutbox(IDocumentTierReliabilitySurface surface)
            => _surface = surface ?? throw new ArgumentNullException(nameof(surface));

        public Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            _ = outboundBrokeredMessages ?? throw new ArgumentNullException(nameof(outboundBrokeredMessages));

            // INVARIANT: the outbox contributes ops to the active framework-owned batch on the surface. There is no
            // active batch outside the Document-Tier Batch-Lifecycle Behavior's next() scope, so a null handle is a
            // wiring error (the outbox was invoked outside the document-tier pipeline) and must fail loudly.
            ICosmosAtomicWriteHandle handle = _surface.CurrentHandle
                ?? throw new InvalidOperationException(
                    "No active document-tier atomic-write handle. The Cosmos outbox can only enqueue inside the Document-Tier Batch-Lifecycle Behavior's batch scope.");

            IReadOnlyList<JsonElement> partitionKeyValues = ResolvePartitionKeyValues(handle.PartitionKey, handle.PartitionKeyPath);

            foreach (OutboundBrokeredMessage outboundBrokeredMessage in outboundBrokeredMessages)
            {
                StageOutboxOp(handle, outboundBrokeredMessage, partitionKeyValues);
            }

            return Task.CompletedTask;
        }

        // Builds the outbox document and stages its CreateItemStream op through the handle (stage-and-count is one
        // indivisible action — the handle's closed-by-construction contract guarantees the op is counted).
        private static void StageOutboxOp(ICosmosAtomicWriteHandle handle, OutboundBrokeredMessage outboundBrokeredMessage, IReadOnlyList<JsonElement> partitionKeyValues)
        {
            CosmosOutboxDocument document = CosmosOutboxDocument.From(outboundBrokeredMessage);
            Stream payload = BuildItemStream(document, handle.PartitionKeyPath, partitionKeyValues);

            // INVARIANT: the outbox create rides the SAME framework-owned batch as the aggregate upsert (atomicity);
            // the outbox doc is a fresh document with no ETag (the aggregate carries IfMatchEtag, the app's concern).
            handle.StageCreateItemStream(payload);
        }

        // Renders the document body with the resolved partition-key value stamped at each container PK path segment.
        private static Stream BuildItemStream(CosmosOutboxDocument document, IReadOnlyList<string> partitionKeyPath, IReadOnlyList<JsonElement> partitionKeyValues)
        {
            var rendered = document.ToJsonObject(partitionKeyPath, partitionKeyValues);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rendered, ChatterJson.Options);
            return new MemoryStream(bytes, writable: false);
        }

        // Recovers the scalar partition-key value(s) from the resolved PartitionKey via its public JSON-array form
        // (e.g. ["tenant-1"] for a single string PK, [42] for a numeric PK, ["a","b"] for a hierarchical PK), mapped
        // positionally onto the path. Each value is returned as a cloned JsonElement — a self-contained value type
        // detached from its backing JsonDocument — preserving its JSON value kind (string/number/bool/null) so the
        // stamped document lands in the SAME logical partition the framework-owned batch was opened on. Callers may
        // pass the same list to ToJsonObject for multiple documents; each call mints a fresh JsonNode from the element.
        private static IReadOnlyList<JsonElement> ResolvePartitionKeyValues(PartitionKey partitionKey, IReadOnlyList<string> partitionKeyPath)
        {
            using var doc = JsonDocument.Parse(partitionKey.ToString());
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Unexpected partition-key serialization '{partitionKey}'; expected a JSON array.");
            }

            if (root.GetArrayLength() != partitionKeyPath.Count)
            {
                throw new InvalidOperationException(
                    $"The resolved partition key has '{root.GetArrayLength()}' value(s) but the container partition-key path declares '{partitionKeyPath.Count}' segment(s).");
            }

            var values = new List<JsonElement>(partitionKeyPath.Count);
            foreach (JsonElement element in root.EnumerateArray())
            {
                // Clone detaches the element from the backing JsonDocument so the value remains valid after the
                // using-scoped doc is disposed and can be reused across multiple ToJsonObject calls.
                values.Add(element.Clone());
            }

            return values;
        }
    }
}
