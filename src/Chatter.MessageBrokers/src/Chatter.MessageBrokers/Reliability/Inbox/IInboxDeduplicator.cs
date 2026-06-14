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
    /// This is a secondary facet obtained by casting the single resolved <see cref="IBrokeredMessageInbox"/>
    /// at the consumption site — not an independent DI service. A custom reliability store must implement both
    /// <see cref="IBrokeredMessageInbox"/> and <see cref="IInboxDeduplicator"/> on one concrete registered
    /// under <see cref="IBrokeredMessageInbox"/>; a custom primary that does not implement this interface
    /// throws <see cref="System.InvalidCastException"/> at the poll site.
    /// </remarks>
    public interface IInboxDeduplicator
    {
        Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default);
    }
}
