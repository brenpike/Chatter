using Chatter.CQRS;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageDispatcher : IExternalDispatcher, IBrokeredMessageSender, IBrokeredMessagePublisher, IBrokeredMessageForwarder
    { }
}
