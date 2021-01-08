using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;

namespace Chatter.MessageBrokers
{
    public class MessagingInfrastructure : IMessagingInfrastructure
    {
        private readonly IMessagingInfrastructureReceiverFactory _receiveInfrastructure;
        private readonly IMessagingInfrastructureDispatcherFactory _dispatchInfrastructure;
        private readonly IBrokeredMessagePathBuilder _pathBuilder;

        public MessagingInfrastructure(string type,
                                       IMessagingInfrastructureReceiverFactory receiveInfrastructure,
                                       IMessagingInfrastructureDispatcherFactory dispatchInfrastructure)
            : this(type, receiveInfrastructure, dispatchInfrastructure, new DefaultBrokeredMessagePathBuilder())
        { }

        public MessagingInfrastructure(string type,
                                       IMessagingInfrastructureReceiverFactory receiveInfrastructure,
                                       IMessagingInfrastructureDispatcherFactory dispatchInfrastructure,
                                       IBrokeredMessagePathBuilder pathBuilder)
        {
            Type = type;
            _receiveInfrastructure = receiveInfrastructure;
            _dispatchInfrastructure = dispatchInfrastructure;
            _pathBuilder = pathBuilder ?? throw new System.ArgumentNullException(nameof(pathBuilder));
        }

        public string Type { get; private set; }
        public IMessagingInfrastructureReceiver ReceiveInfrastructure => _receiveInfrastructure.Create();
        public IMessagingInfrastructureDispatcher DispatchInfrastructure => _dispatchInfrastructure.Create();
        public IBrokeredMessagePathBuilder PathBuilder => _pathBuilder;
    }
}
