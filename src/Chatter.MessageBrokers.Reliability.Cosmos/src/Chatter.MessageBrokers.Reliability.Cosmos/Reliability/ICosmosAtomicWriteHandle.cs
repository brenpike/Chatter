using Chatter.MessageBrokers.Reliability;
using Microsoft.Azure.Cosmos;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The document-tier atomic-write handle — the doc-tier sibling of <see cref="IPersistanceTransaction"/> on the
    /// document-tier reliability surface. Satisfies the tier-neutral <see cref="IAtomicWriteHandle"/> marker so the
    /// shared enqueue contract (<c>SendToOutbox</c>) is abstracted over it. It carries the document-store primitives the
    /// relational-shaped <see cref="MessageBrokers.Context.TransactionContext"/> does not and cannot carry: the bound
    /// Cosmos <see cref="Container"/>, the open <see cref="TransactionalBatch"/>, the resolved partition-key value, and
    /// the container's partition-key path.
    /// </summary>
    /// <remarks>
    /// The handle is exposed via the document-tier surface / DI scope by the Document-Tier Batch-Lifecycle Behavior
    /// before <c>next()</c> — it is NOT stuffed into <see cref="MessageBrokers.Context.TransactionContext.Container"/>
    /// (ADR-0006 surface-ownership amendment). No op-staging members are declared in #218: #219 (outbox) and #220
    /// (inbox) contribute ops to <see cref="Batch"/>; the relay (#222) consumes the lease container registered in DI.
    /// </remarks>
    public interface ICosmosAtomicWriteHandle : IAtomicWriteHandle
    {
        /// <summary>
        /// The application-injected Cosmos container the aggregate, outbox doc, and inbox marker are co-resident in.
        /// </summary>
        Container Container { get; }

        /// <summary>
        /// The open, framework-owned <see cref="TransactionalBatch"/> scoped to <see cref="PartitionKey"/>. Handler
        /// aggregate ops and the shared enqueue contract contribute ops to this batch; the Document-Tier
        /// Batch-Lifecycle Behavior executes it once after <c>next()</c> returns (the single commit point).
        /// </summary>
        TransactionalBatch Batch { get; }

        /// <summary>
        /// The resolved partition-key value for the in-flight message's aggregate partition, produced by the
        /// app-registered partition-key resolver.
        /// </summary>
        PartitionKey PartitionKey { get; }

        /// <summary>
        /// The container's partition-key path (e.g. <c>"/tenantId"</c>; may be hierarchical). #219/#220 write the
        /// resolved partition-key value at this actual container PK path rather than at a fixed field named
        /// <c>"partitionKey"</c>.
        /// </summary>
        // INVARIANT: PartitionKeyPath is the container's declared PK path, carried so document writers stamp the
        // resolved value at the real path; a hierarchical container exposes multiple path segments.
        IReadOnlyList<string> PartitionKeyPath { get; }

        /// <summary>
        /// Optional ETag concurrency token for the document-tier aggregate upsert. No behavior in #218; the
        /// document writers in #219/#220 apply it as <c>IfMatchEtag</c>.
        /// </summary>
        string ETag { get; }
    }
}
