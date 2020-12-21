using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;

namespace Chatter.MessageBrokers
{
    public class MessagingInfrastructure : IMessagingInfrastructure
    {
        private readonly IMessagingInfrastructureReceiverFactory _receiveInfrastructure;
        private readonly IMessagingInfrastructureDispatcherFactory _dispatchInfrastructure;

        public MessagingInfrastructure(string type,
                                       IMessagingInfrastructureReceiverFactory receiveInfrastructure,
                                       IMessagingInfrastructureDispatcherFactory dispatchInfrastructure)
        {
            Type = type;
            _receiveInfrastructure = receiveInfrastructure;
            _dispatchInfrastructure = dispatchInfrastructure;
        }

        public string Type { get; private set; }
        public IMessagingInfrastructureReceiver ReceiveInfrastructure => _receiveInfrastructure.Create();
        public IMessagingInfrastructureDispatcher DispatchInfrastructure => _dispatchInfrastructure.Create();
    }
}
