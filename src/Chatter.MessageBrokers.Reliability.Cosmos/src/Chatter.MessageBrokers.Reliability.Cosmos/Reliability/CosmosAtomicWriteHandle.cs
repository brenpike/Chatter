using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The concrete document-tier atomic-write handle. Constructed by the Document-Tier Batch-Lifecycle Behavior after
    /// it resolves the partition key and opens the <see cref="TransactionalBatch"/>; exposed on the document-tier
    /// surface for the duration of <c>next()</c>. Carries no op-staging behavior in #218.
    /// </summary>
    internal sealed class CosmosAtomicWriteHandle : ICosmosAtomicWriteHandle
    {
        public CosmosAtomicWriteHandle(Container container, TransactionalBatch batch, PartitionKey partitionKey, IReadOnlyList<string> partitionKeyPath, string eTag = null)
        {
            Container = container ?? throw new ArgumentNullException(nameof(container));
            Batch = batch ?? throw new ArgumentNullException(nameof(batch));
            PartitionKey = partitionKey;
            PartitionKeyPath = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));
            ETag = eTag;
        }

        public Container Container { get; }

        public TransactionalBatch Batch { get; }

        public PartitionKey PartitionKey { get; }

        public IReadOnlyList<string> PartitionKeyPath { get; }

        public string ETag { get; }

        /// <summary>
        /// Count of ops staged into <see cref="Batch"/> by handler aggregate writes, the outbox, and the inbox marker
        /// (#219/#220). The Document-Tier Batch-Lifecycle Behavior reads this after <c>next()</c> and skips the single
        /// batch-execute when it is zero, so an empty batch never calls the Cosmos transport (#218 stages nothing).
        /// </summary>
        public int StagedOperationCount { get; private set; }

        /// <summary>
        /// Records that an op-contributor staged an operation into <see cref="Batch"/>. Invoked by #219/#220 op
        /// contributors after they call a <see cref="TransactionalBatch"/> op method.
        /// </summary>
        public void MarkOperationStaged() => StagedOperationCount++;
    }
}
