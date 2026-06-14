using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    /// <summary>
    /// The tier-neutral inbox dedup contract expressing once-only-handling intent, implemented by both reliability
    /// tiers. Distinct from <see cref="IBrokeredMessageInbox.ReceiveViaInbox"/>, which is the relational-only wrap
    /// seam; this contract carries only the shared once-only intent.
    /// </summary>
    public interface IInboxDeduplicator
    {
        Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default);
    }
}
