using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;

namespace Chatter.MessageBrokers
{
    public interface IMessagingInfrastructureProvider
    {
        IMessagingInfrastructure GetInfrastructure(string type);
        IMessagingInfrastructureReceiver GetReceiver(string type);
        IMessagingInfrastructureDispatcher GetDispatcher(string type);
    }
}
