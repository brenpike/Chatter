namespace Chatter.MessageBrokers.Sending
{
    public interface IMessagingInfrastructureDispatcherFactory //TODO: need default impl. in messagebroker library
    {
        IMessagingInfrastructureDispatcher Create();
    }
}
