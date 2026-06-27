#nullable enable annotations
using Chatter.MessageBrokers.Sending;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// An application-supplied seam the #222 change-feed outbox relay calls, for an admitted pending outbox document,
    /// INSTEAD of reconstructing the brokered message verbatim from the persisted outbox fields. It lets the application
    /// own the wire message it ultimately publishes (e.g. re-hydrate the body from a domain aggregate, re-shape the
    /// payload, or suppress the publish) while the relay keeps ownership of the change-feed drain, the at-least-once
    /// checkpoint posture, and the delivered/TTL stamp.
    /// </summary>
    /// <remarks>
    /// Contract honored by the relay (see <see cref="CosmosOutboxRelay"/>):
    /// <list type="bullet">
    /// <item>Called exactly once per admitted pending document (a document failing the relay's pending filter is never
    /// resolved).</item>
    /// <item>A NON-NULL result is dispatched through the same no-reliability-re-entry path the verbatim reconstruction
    /// uses; the document is then stamped delivered+TTL.</item>
    /// <item>A NULL result dispatches NOTHING, yet the document is STILL stamped delivered+TTL (an intentional
    /// drop-and-acknowledge).</item>
    /// <item>A THROW propagates out of the relay with NO stamp issued, so the host does not checkpoint the change-feed
    /// batch and the document re-surfaces next pass (at-least-once).</item>
    /// </list>
    /// </remarks>
    public interface IOutboxBodyResolver
    {
        /// <summary>
        /// Resolves the brokered message to publish for a single admitted pending outbox document described by
        /// <paramref name="context"/>, or <c>null</c> to publish nothing (the document is still acknowledged delivered).
        /// </summary>
        Task<OutboundBrokeredMessage?> ResolveAsync(OutboxDrainContext context, CancellationToken cancellationToken = default);
    }
}
