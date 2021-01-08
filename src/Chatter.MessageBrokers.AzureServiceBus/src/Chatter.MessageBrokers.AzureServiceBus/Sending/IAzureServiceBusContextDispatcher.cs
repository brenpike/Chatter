using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    /// <summary>
    /// A context dispatcher for Azure Service Bus
    /// </summary>
    public interface IAzureServiceBusContextDispatcher : IMessageBrokerContextPublisher, IMessageBrokerContextSender, IMessageBrokerContextForwarder
    {
    }
}
