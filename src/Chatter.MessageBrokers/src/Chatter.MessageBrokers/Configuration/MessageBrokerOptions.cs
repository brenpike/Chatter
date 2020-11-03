using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Reliability.Configuration;

namespace Chatter.MessageBrokers.Configuration
{
    public class MessageBrokerOptions
    {
        public TransactionMode TransactionMode { get; set; } = TransactionMode.ReceiveOnly;

        public ReliabilityOptions Reliability { get; set; }
        public RecoveryOptions Recovery { get; set; }
    }
}
