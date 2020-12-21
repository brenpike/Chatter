using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;

namespace Chatter.MessageBrokers
{
    public interface IMessagingInfrastructure
    {
        string Type { get; }
        IMessagingInfrastructureReceiver ReceiveInfrastructure { get; }
        IMessagingInfrastructureDispatcher DispatchInfrastructure { get; }
    }
}
