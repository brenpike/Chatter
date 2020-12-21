using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;

namespace Chatter.MessageBrokers
{
    public interface IMessagingInfrastructureProvider
    {
        IMessagingInfrastructure Get(string type);
        IMessagingInfrastructureReceiver GetReceiver(string type);
        IMessagingInfrastructureDispatcher GetDispatcher(string type);
    }
}
