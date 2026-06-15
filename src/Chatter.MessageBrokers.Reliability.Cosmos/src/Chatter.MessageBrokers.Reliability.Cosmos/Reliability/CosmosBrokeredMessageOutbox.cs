using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
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

            // INVARIANT: the outbox authors a reserved outbox:-prefixed id, so it must stage through the internal
            // reserved-write facet that BYPASSES the public reserved-namespace guard (the public StageCreateItemStream
            // would reject its own outbox: id). The active handle is always the framework's CosmosAtomicWriteHandle,
            // which implements this facet; a handle that does not is a wiring error and must fail loudly.
            ICosmosReservedWriteHandle reservedHandle = handle as ICosmosReservedWriteHandle
                ?? throw new InvalidOperationException(
                    "The active document-tier atomic-write handle does not support reserved-id staging. The Cosmos outbox " +
                    "requires the framework handle that implements ICosmosReservedWriteHandle to author its reserved outbox: id.");

            IReadOnlyList<JsonElement> partitionKeyValues = CosmosPartitionKeyStamping.RecoverPartitionKeyValues(handle.PartitionKey, handle.PartitionKeyPath);

            foreach (OutboundBrokeredMessage outboundBrokeredMessage in outboundBrokeredMessages)
            {
                StageOutboxOp(handle, reservedHandle, outboundBrokeredMessage, partitionKeyValues);
            }

            return Task.CompletedTask;
        }

        // Builds the outbox document and stages its reserved CreateItemStream op through the reserved-write facet
        // (stage-and-count is one indivisible action — the handle's closed-by-construction contract guarantees the op is
        // counted). The reserved facet bypasses the public reserved-namespace guard so the outbox: id is accepted.
        private static void StageOutboxOp(ICosmosAtomicWriteHandle handle, ICosmosReservedWriteHandle reservedHandle, OutboundBrokeredMessage outboundBrokeredMessage, IReadOnlyList<JsonElement> partitionKeyValues)
        {
            CosmosOutboxDocument document = CosmosOutboxDocument.From(outboundBrokeredMessage);
            Stream payload = BuildItemStream(document, handle.PartitionKeyPath, partitionKeyValues);

            // INVARIANT: the outbox create rides the SAME framework-owned batch as the aggregate upsert (atomicity);
            // the outbox doc is a fresh document with no ETag (the aggregate carries IfMatchEtag, the app's concern).
            reservedHandle.StageReservedCreateItemStream(payload);
        }

        // Renders the document body with the resolved partition-key value stamped at each container PK path segment.
        private static Stream BuildItemStream(CosmosOutboxDocument document, IReadOnlyList<string> partitionKeyPath, IReadOnlyList<JsonElement> partitionKeyValues)
        {
            var rendered = document.ToJsonObject(partitionKeyPath, partitionKeyValues);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rendered, ChatterJson.Options);
            return new MemoryStream(bytes, writable: false);
        }
    }
}
