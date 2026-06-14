using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    /// <summary>
    /// The tier-neutral inbox dedup contract expressing once-only-handling intent, implemented by both reliability
    /// tiers. Distinct from <see cref="IBrokeredMessageInbox.ReceiveViaInbox"/>, which is the relational-only wrap
    /// seam; this contract carries only the shared once-only intent.
    /// </summary>
    /// <remarks>
    /// A custom reliability store that satisfies both <see cref="IBrokeredMessageInbox"/> and
    /// <see cref="IInboxDeduplicator"/> must be registered under <see cref="IBrokeredMessageInbox"/> as a
    /// single concrete at <c>Scoped</c> or <c>Singleton</c> lifetime. The framework forwards
    /// <see cref="IInboxDeduplicator"/> to the same registration at the primary's lifetime so both facets
    /// always resolve to the same instance. A <c>Transient</c> custom primary is rejected at registration time
    /// because DI has no primitive to guarantee same-instance resolution across two Transient resolutions.
    /// A consumer that registers both facets independently as separate descriptors owns ensuring the two
    /// registrations agree; the framework does not merge them.
    /// </remarks>
    public interface IInboxDeduplicator
    {
        Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default);
    }
}
