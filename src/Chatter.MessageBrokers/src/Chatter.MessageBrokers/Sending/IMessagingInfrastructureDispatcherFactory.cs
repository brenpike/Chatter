namespace Chatter.MessageBrokers.Sending
{
    public interface IMessagingInfrastructureDispatcherFactory
    {
        IMessagingInfrastructureDispatcher Create();
    }
}
