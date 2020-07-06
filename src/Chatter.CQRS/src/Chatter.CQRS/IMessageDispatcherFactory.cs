namespace Chatter.CQRS
{
    public interface IMessageDispatcherFactory
    {
        IMessageDispatcher CreateDispatcher<TMessage>() where TMessage : IMessage;
    }
}
