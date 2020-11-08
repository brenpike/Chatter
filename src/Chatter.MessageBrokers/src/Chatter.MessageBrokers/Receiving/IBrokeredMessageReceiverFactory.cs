using Chatter.CQRS;

namespace Chatter.MessageBrokers.Receiving
{
    public interface IBrokeredMessageReceiverFactory
    {
        IBrokeredMessageReceiver<TMessage> Create<TMessage>(ReceiverOptions options) where TMessage : class, IMessage;
    }
}
