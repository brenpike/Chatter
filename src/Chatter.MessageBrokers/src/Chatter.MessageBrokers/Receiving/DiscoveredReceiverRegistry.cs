using System.Collections.Generic;

namespace Chatter.MessageBrokers.Receiving
{
    // INVARIANT: retains the LIVE ReceiverOptions reference (never a copy) for every discovered/registered
    // receiver, so a later mutation by an infrastructure package (e.g. stamping an infrastructure-resolved
    // value) is visible when the receiver reads its options at init. Infrastructure-agnostic: this type holds
    // no knowledge of any specific messaging infrastructure.
    public sealed class DiscoveredReceiverRegistry : IDiscoveredReceiverRegistry
    {
        private readonly List<ReceiverOptions> _receivers = new List<ReceiverOptions>();

        public void Register(ReceiverOptions options)
        {
            _receivers.Add(options);
        }

        public IReadOnlyCollection<ReceiverOptions> DiscoveredReceivers => _receivers;
    }
}
