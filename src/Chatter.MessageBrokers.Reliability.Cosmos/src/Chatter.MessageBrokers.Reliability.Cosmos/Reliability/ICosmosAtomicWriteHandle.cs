using Chatter.MessageBrokers.Reliability;
using Microsoft.Azure.Cosmos;
using System.Collections.Generic;
using System.IO;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The document-tier atomic-write handle — the doc-tier sibling of <see cref="IPersistanceTransaction"/> on the
    /// document-tier reliability surface. Satisfies the tier-neutral <see cref="IAtomicWriteHandle"/> marker so the
    /// shared enqueue contract (<c>SendToOutbox</c>) is abstracted over it. It carries the document-store primitives the
    /// relational-shaped <see cref="MessageBrokers.Context.TransactionContext"/> does not and cannot carry: the bound
    /// Cosmos <see cref="Container"/>, the resolved partition-key value, and the container's partition-key path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handle is exposed via the document-tier surface / DI scope by the Document-Tier Batch-Lifecycle Behavior
    /// before <c>next()</c> — it is NOT stuffed into <see cref="MessageBrokers.Context.TransactionContext.Container"/>
    /// (ADR-0006 surface-ownership amendment).
    /// </para>
    /// <para>
    /// <strong>Closed-by-construction staging contract.</strong> The framework-owned <see cref="Microsoft.Azure.Cosmos.TransactionalBatch"/>
    /// is NOT publicly reachable for op-adds. All staging goes through the intent-revealing methods on this interface
    /// (<see cref="StageCreateItemStream"/>, <see cref="StageReplaceItem{T}"/>, <see cref="StagePatchItem"/>), each of
    /// which calls the corresponding batch op AND increments <see cref="StagedOperationCount"/> in one indivisible
    /// action. It is therefore impossible to stage an op without it being counted — the ack-on-uncommitted class
    /// (stage-without-mark → empty-batch guard reads 0 → skips <c>ExecuteAsync</c> → acks with nothing committed) is
    /// closed by construction.
    /// </para>
    /// <para>
    /// <strong>Reserved-namespace guard (part of the contract).</strong> The only public create/upsert path is
    /// <see cref="StageCreateItemStream"/>, whose guard keys on the persisted item.id PEEKED from the JSON payload the
    /// SDK reads, so no public create/upsert can stage a document whose persisted physical id carries a reserved
    /// <c>inbox:</c>/<c>outbox:</c> prefix — the guard validates the value Cosmos actually persists, not a parameter that
    /// could diverge from it. <see cref="StageReplaceItem{T}"/>/<see cref="StagePatchItem"/> guard their explicit op-key
    /// <c>id</c> (which IS the persisted key) via <see cref="CosmosItemId.GuardNotReserved"/> BEFORE the op is staged
    /// (parity with the trusted <c>_chatterType</c> reserved field). The guard is what makes the inbox marker-409 →
    /// confirmed-duplicate inference collision-proof: an app doc could otherwise author a reserved-prefix id, 409 the
    /// framework's marker create, and be silently swallowed as a duplicate. The framework's OWN reserved-id writes flow
    /// through the internal <see cref="ICosmosReservedWriteHandle"/>, which BYPASSES the guard — it is the sole
    /// sanctioned reserved-id writer.
    /// </para>
    /// <para>
    /// #219 (outbox) uses <see cref="StageCreateItemStream"/> for the Chatter-owned wire shape.
    /// #220 (inbox marker) uses <see cref="StageCreateItemStream"/>;
    /// a 409 confirmed-duplicate still surfaces via <see cref="CosmosBatchExecutionException.StatusCode"/> (unchanged).
    /// #222 (relay) consumes the lease container registered in DI.
    /// </para>
    /// </remarks>
    public interface ICosmosAtomicWriteHandle : IAtomicWriteHandle
    {
        /// <summary>
        /// The application-injected Cosmos container the aggregate, outbox doc, and inbox marker are co-resident in.
        /// </summary>
        Container Container { get; }

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
        /// Optional ETag concurrency token for the document-tier aggregate upsert. No behavior in #218/#219; the
        /// document writers in #220 apply it as <c>IfMatchEtag</c>.
        /// </summary>
        string ETag { get; }

        /// <summary>
        /// Count of ops staged through this handle. The Document-Tier Batch-Lifecycle Behavior reads this after
        /// <c>next()</c> and skips the single batch-execute when it is zero, so an empty batch never calls the
        /// Cosmos transport. Every staging method increments this count atomically with the batch op-add — it is
        /// the behavior's empty-batch guard's single source of truth.
        /// </summary>
        int StagedOperationCount { get; }

        /// <summary>
        /// Stages a <see cref="Microsoft.Azure.Cosmos.TransactionalBatch.CreateItemStream"/> op for an app-owned
        /// wire-format document (<paramref name="payload"/>) and increments <see cref="StagedOperationCount"/>. The id is
        /// PEEKED from the JSON payload and rejected via <see cref="CosmosItemId.GuardNotReserved"/> if it is in the
        /// reserved <c>inbox:</c>/<c>outbox:</c> namespace; a payload with no/unparseable/non-object id is treated as
        /// idless (non-reserved) and passes. The framework's own reserved-id stream writes use
        /// <see cref="ICosmosReservedWriteHandle.StageReservedCreateItemStream"/> (guard-bypassing) instead.
        /// </summary>
        void StageCreateItemStream(Stream payload, TransactionalBatchItemRequestOptions requestOptions = null);

        /// <summary>
        /// Stages a <see cref="Microsoft.Azure.Cosmos.TransactionalBatch.ReplaceItem{T}"/> op for a typed
        /// <paramref name="item"/> and increments <see cref="StagedOperationCount"/>. <paramref name="id"/> is rejected
        /// via <see cref="CosmosItemId.GuardNotReserved"/> when it is reserved.
        /// </summary>
        void StageReplaceItem<T>(string id, T item, TransactionalBatchItemRequestOptions requestOptions = null);

        /// <summary>
        /// Stages a <see cref="Microsoft.Azure.Cosmos.TransactionalBatch.PatchItem"/> op for the document identified
        /// by <paramref name="id"/> and increments <see cref="StagedOperationCount"/>. <paramref name="id"/> is rejected
        /// via <see cref="CosmosItemId.GuardNotReserved"/> when it is reserved.
        /// </summary>
        void StagePatchItem(string id, IReadOnlyList<PatchOperation> patchOperations, TransactionalBatchPatchItemRequestOptions requestOptions = null);
    }

    /// <summary>
    /// The framework-only reserved-id staging facet of the document-tier atomic-write handle. The reserved-namespace
    /// guard on <see cref="ICosmosAtomicWriteHandle"/>'s public staging methods rejects <c>inbox:</c>/<c>outbox:</c>
    /// ids; the framework's OWN reserved-id writes (the outbox doc #219, the inbox marker #220) must therefore stage
    /// through this internal facet, which BYPASSES the guard. It is the framework's sole sanctioned reserved-id writer.
    /// </summary>
    /// <remarks>
    /// INVARIANT: only Chatter-internal code (the outbox / inbox-marker staging paths) may resolve and use this facet,
    /// so the public surface cannot be used to author a reserved-prefix id while the framework still can. The concrete
    /// <see cref="CosmosAtomicWriteHandle"/> implements both this interface and <see cref="ICosmosAtomicWriteHandle"/>;
    /// reserved staging still increments <see cref="ICosmosAtomicWriteHandle.StagedOperationCount"/> so the empty-batch
    /// guard's single-source-of-truth invariant is preserved.
    /// </remarks>
    internal interface ICosmosReservedWriteHandle
    {
        /// <summary>
        /// Stages a <see cref="Microsoft.Azure.Cosmos.TransactionalBatch.CreateItemStream"/> op for a Chatter-owned
        /// reserved-id wire-format document (<paramref name="payload"/>) WITHOUT applying the reserved-namespace guard,
        /// and increments <see cref="ICosmosAtomicWriteHandle.StagedOperationCount"/>. Used by the outbox (#219) and the
        /// inbox marker (#220) to author their reserved <c>outbox:</c>/<c>inbox:</c>-prefixed ids.
        /// </summary>
        void StageReservedCreateItemStream(Stream payload, TransactionalBatchItemRequestOptions requestOptions = null);
    }
}
