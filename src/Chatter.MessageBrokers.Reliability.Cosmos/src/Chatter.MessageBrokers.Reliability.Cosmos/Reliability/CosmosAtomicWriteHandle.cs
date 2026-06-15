using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.IO;

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
    /// (<see cref="StageCreateItemStream"/>, <see cref="StageCreateItem{T}"/>, <see cref="StageUpsertItem{T}"/>,
    /// <see cref="StageReplaceItem{T}"/>, <see cref="StagePatchItem"/>), each of which delegates to the corresponding
    /// SDK op method on the private batch AND increments <see cref="StagedOperationCount"/> in one method body. No
    /// caller can stage an op without it being counted.
    /// </remarks>
    internal sealed class CosmosAtomicWriteHandle : ICosmosAtomicWriteHandle
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
            _batch.CreateItemStream(payload, requestOptions);
            StagedOperationCount++;
        }

        /// <inheritdoc/>
        public void StageCreateItem<T>(T item, TransactionalBatchItemRequestOptions requestOptions = null)
        {
            _batch.CreateItem(item, requestOptions);
            StagedOperationCount++;
        }

        /// <inheritdoc/>
        public void StageUpsertItem<T>(T item, TransactionalBatchItemRequestOptions requestOptions = null)
        {
            _batch.UpsertItem(item, requestOptions);
            StagedOperationCount++;
        }

        /// <inheritdoc/>
        public void StageReplaceItem<T>(string id, T item, TransactionalBatchItemRequestOptions requestOptions = null)
        {
            _batch.ReplaceItem(id, item, requestOptions);
            StagedOperationCount++;
        }

        /// <inheritdoc/>
        public void StagePatchItem(string id, IReadOnlyList<PatchOperation> patchOperations, TransactionalBatchPatchItemRequestOptions requestOptions = null)
        {
            _batch.PatchItem(id, patchOperations, requestOptions);
            StagedOperationCount++;
        }
    }
}
