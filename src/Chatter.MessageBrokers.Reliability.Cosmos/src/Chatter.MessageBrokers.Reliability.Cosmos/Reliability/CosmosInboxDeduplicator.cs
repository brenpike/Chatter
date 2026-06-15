using Chatter.MessageBrokers.Reliability.Inbox;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Realizes the tier-neutral <see cref="IInboxDeduplicator"/> contract TYPE for the Cosmos document tier — for
    /// parity and discoverability with the relational tier — but is intentionally NOT DI-registered. The document
    /// tier does NOT achieve once-only handling via a <see cref="HasBeenReceived"/> read: it stamps a co-resident
    /// <see cref="CosmosInboxMarker"/> into the framework-owned <c>TransactionalBatch</c> in the
    /// <see cref="DocumentTierBatchLifecycleBehavior{TMessage}"/> BEFORE the handler runs, and a 409-on-create of that
    /// marker at batch-execute time is the confirmed-duplicate signal (closed-by-construction; all-or-nothing; no
    /// read-then-add, so no TOCTOU). A <see cref="HasBeenReceived"/> read would be exactly that TOCTOU read — a
    /// separate existence check whose result can be stale by the time the write commits — so it is unsupported here.
    /// </summary>
    /// <remarks>
    /// This type is NOT registered in DI (see the Cosmos DI <c>Extensions</c>): the document tier never resolves
    /// <see cref="IBrokeredMessageInbox"/> / <see cref="IInboxDeduplicator"/> (those are a cast-facet of the single
    /// resolved reliability store, which the document tier does not register). Registering this shim would violate
    /// core's resolves-to-null cast-at-consumption invariant by introducing a second, independent
    /// <see cref="IInboxDeduplicator"/> service. It exists solely so the contract is realized as a type on the Cosmos
    /// tier for parity; invoking <see cref="HasBeenReceived"/> is a programming error and throws.
    /// </remarks>
    public sealed class CosmosInboxDeduplicator : IInboxDeduplicator
    {
        /// <summary>
        /// Not supported on the Cosmos document tier. Always throws <see cref="NotSupportedException"/>: document-tier
        /// dedup is the co-resident inbox marker contributed to the framework-owned batch plus a 409-at-execute on the
        /// marker op (closed-by-construction, no read-then-add/TOCTOU), not a <see cref="HasBeenReceived"/> read.
        /// </summary>
        public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "The Cosmos document tier does not dedup via HasBeenReceived. It stamps a co-resident inbox marker into " +
                "the framework-owned transactional batch before the handler runs; a 409-on-create of the marker at " +
                "batch-execute is the confirmed-duplicate signal (closed-by-construction, no read-then-add/TOCTOU). A " +
                "HasBeenReceived read would reintroduce the TOCTOU this design eliminates, so it is unsupported.");
    }
}
