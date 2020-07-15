using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;

namespace Chatter.MessageBrokers.Reliability
{
    public interface IReliableBrokeredMessageProcessor : IBrokeredMessageInbox, IBrokeredMessageOutbox
    {
        bool IsReliableMesageProcessingEnabled { get; }
    }
}
