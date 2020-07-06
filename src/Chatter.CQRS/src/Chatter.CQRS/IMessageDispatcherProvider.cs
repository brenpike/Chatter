using System;

namespace Chatter.CQRS
{
    public interface IMessageDispatcherProvider
    {
        Type DispatchType { get; }
        IMessageDispatcher CreateDispatcher<TMessage>() where TMessage : IMessage;
    }
}
