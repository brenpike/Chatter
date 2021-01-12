using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Reliability.Configuration;

namespace Chatter.MessageBrokers.Configuration
{
    public class MessageBrokerOptions
    {
        public TransactionMode TransactionMode { get; internal set; }
        public ReliabilityOptions Reliability { get; internal set; }
        public RecoveryOptions Recovery { get; internal set; }
    }
}
