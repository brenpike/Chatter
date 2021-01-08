using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.SqlServiceBroker.Sending
{
    /// <summary>
    /// A context dispatcher for Sql Service Broker
    /// </summary>
    public interface ISqlServiceBrokerContextDispatcher : IMessageBrokerContextPublisher, IMessageBrokerContextSender, IMessageBrokerContextForwarder
    {
    }
}
