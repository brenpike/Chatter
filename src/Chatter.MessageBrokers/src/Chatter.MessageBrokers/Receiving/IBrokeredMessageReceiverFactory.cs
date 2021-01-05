using Chatter.CQRS;

namespace Chatter.MessageBrokers.Receiving
{
    public interface IBrokeredMessageReceiverFactory
    {
        IBrokeredMessageReceiver<TMessage> Create<TMessage>() where TMessage : class, IMessage;
    }
}
