namespace Chatter.MessageBrokers.Receiving
{
    public interface IMessagingInfrastructureReceiverFactory
    {
        IMessagingInfrastructureReceiver Create();
    }
}
