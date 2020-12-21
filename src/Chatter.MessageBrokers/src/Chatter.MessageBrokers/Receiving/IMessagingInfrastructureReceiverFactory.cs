namespace Chatter.MessageBrokers.Receiving
{
    public interface IMessagingInfrastructureReceiverFactory //TODO: need default impl. in messagebroker library
    {
        IMessagingInfrastructureReceiver Create();
    }
}
