using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The concrete document-tier atomic-write handle. Constructed by the Document-Tier Batch-Lifecycle Behavior after
    /// it resolves the partition key and opens the <see cref="TransactionalBatch"/>; exposed on the document-tier
    /// surface for the duration of <c>next()</c>.
    /// </summary>
    /// <remarks>
    /// <strong>Closed-by-construction staging contract.</strong> The <see cref="TransactionalBatch"/> is held as a
    /// private field and is not publicly reachable for op-adds. All op-adds are funneled through the staging methods
    /// (<see cref="StageCreateItemStream"/>, <see cref="StageReplaceItem{T}"/>, <see cref="StagePatchItem"/>), each of
    /// which delegates to the corresponding SDK op method on the private batch AND increments
    /// <see cref="StagedOperationCount"/> in one method body. No caller can stage an op without it being counted.
    /// <para>
    /// <strong>Reserved-namespace guard.</strong> The only public create/upsert path is
    /// <see cref="StageCreateItemStream"/>, whose guard keys on the persisted item.id peeked from the payload bytes the
    /// SDK reads, so no public create/upsert can stage a document whose persisted physical id carries a reserved
    /// prefix. <see cref="StageReplaceItem{T}"/>/<see cref="StagePatchItem"/> call
    /// <see cref="CosmosItemId.GuardNotReserved"/> on their explicit op-key <c>id</c> (which IS the persisted key)
    /// BEFORE staging, so a rejected stage leaves the batch and <see cref="StagedOperationCount"/> unperturbed (the
    /// guard fires before the op is added). The framework's own reserved-id writes go through the internal
    /// <see cref="ICosmosReservedWriteHandle"/> facet this type also implements, which stages WITHOUT the guard.
    /// </para>
    /// </remarks>
    internal sealed class CosmosAtomicWriteHandle : ICosmosAtomicWriteHandle, ICosmosReservedWriteHandle
    {
        private readonly TransactionalBatch _batch;

        public CosmosAtomicWriteHandle(Container container, TransactionalBatch batch, PartitionKey partitionKey, IReadOnlyList<string> partitionKeyPath, string eTag = null)
        {
            Container = container ?? throw new ArgumentNullException(nameof(container));
            _batch = batch ?? throw new ArgumentNullException(nameof(batch));
            PartitionKey = partitionKey;
            PartitionKeyPath = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));
            ETag = eTag;
        }

        public Container Container { get; }

        public PartitionKey PartitionKey { get; }

        public IReadOnlyList<string> PartitionKeyPath { get; }

        public string ETag { get; }

        /// <summary>
        /// Count of ops staged through this handle's staging methods. The Document-Tier Batch-Lifecycle Behavior reads
        /// this after <c>next()</c> and skips the single batch-execute when it is zero.
        /// </summary>
        public int StagedOperationCount { get; private set; }

        /// <inheritdoc/>
        public void StageCreateItemStream(Stream payload, TransactionalBatchItemRequestOptions requestOptions = null)
        {
            // INVARIANT: the guard fires BEFORE the op is staged, so a rejected reserved-prefix id leaves the batch and
            // StagedOperationCount unperturbed. The id is peeked from the JSON payload without corrupting the stream the
            // SDK later reads (an idless/unparseable/non-object/empty payload is treated as non-reserved and passes).
            Stream guardedPayload = GuardStreamPayloadNotReserved(payload);
            _batch.CreateItemStream(guardedPayload, requestOptions);
            StagedOperationCount++;
        }

        /// <inheritdoc/>
        public void StageReplaceItem<T>(string id, T item, TransactionalBatchItemRequestOptions requestOptions = null)
        {
            CosmosItemId.GuardNotReserved(id, nameof(id));
            _batch.ReplaceItem(id, item, requestOptions);
            StagedOperationCount++;
        }

        /// <inheritdoc/>
        public void StagePatchItem(string id, IReadOnlyList<PatchOperation> patchOperations, TransactionalBatchPatchItemRequestOptions requestOptions = null)
        {
            CosmosItemId.GuardNotReserved(id, nameof(id));
            _batch.PatchItem(id, patchOperations, requestOptions);
            StagedOperationCount++;
        }

        /// <inheritdoc/>
        void ICosmosReservedWriteHandle.StageReservedCreateItemStream(Stream payload, TransactionalBatchItemRequestOptions requestOptions)
        {
            // The framework's sanctioned reserved-id writer: stages WITHOUT the reserved-namespace guard so the
            // outbox/inbox-marker reserved-prefix ids commit, while still counting the op (closed-by-construction).
            _batch.CreateItemStream(payload, requestOptions);
            StagedOperationCount++;
        }

        // Peeks the "id" property from the JSON payload and applies the reserved-namespace guard, returning a stream the
        // SDK can read from position 0. A non-seekable payload is buffered into a MemoryStream so the SDK reads the same
        // bytes; a seekable payload is read then rewound. A missing/non-string id, a non-object/empty/unparseable
        // payload, or a null payload is treated as idless (non-reserved) and passes.
        private static Stream GuardStreamPayloadNotReserved(Stream payload)
        {
            if (payload is null)
            {
                return payload;
            }

            Stream seekablePayload = payload.CanSeek ? payload : BufferStream(payload);
            long originalPosition = seekablePayload.Position;
            try
            {
                string id = TryReadIdFromJson(seekablePayload);
                CosmosItemId.GuardNotReserved(id, "id");
            }
            finally
            {
                seekablePayload.Position = originalPosition;
            }

            return seekablePayload;
        }

        // Reads the payload fully into a rewound MemoryStream so a non-seekable stream can be peeked then handed to the
        // SDK without losing bytes.
        private static MemoryStream BufferStream(Stream payload)
        {
            var buffer = new MemoryStream();
            payload.CopyTo(buffer);
            buffer.Position = 0;
            return buffer;
        }

        // Returns the top-level "id" string from the JSON payload, or null when the payload is empty, not a JSON object,
        // unparseable, or carries no string-valued "id" — all of which are non-reserved (idless) by treatment.
        private static string TryReadIdFromJson(Stream seekablePayload)
        {
            if (seekablePayload.Length - seekablePayload.Position <= 0)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(seekablePayload);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (document.RootElement.TryGetProperty(CosmosOutboxDocument.IdField, out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.String)
                {
                    return idElement.GetString();
                }

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
