using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public interface IBrokeredMessageInbox
    {
        Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver);
    }
}
