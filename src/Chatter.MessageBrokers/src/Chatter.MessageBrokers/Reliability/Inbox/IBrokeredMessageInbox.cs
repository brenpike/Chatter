using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public interface IBrokeredMessageInbox
    {
        Task Receive<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<TMessage, IMessageBrokerContext, Task> messageReceiver);
    }
}
