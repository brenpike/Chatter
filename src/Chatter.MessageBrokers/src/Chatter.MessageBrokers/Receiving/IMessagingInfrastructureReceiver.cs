using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    public interface IMessagingInfrastructureReceiver<TMessage> where TMessage : class, IMessage
    {
        void StartReceiver(Func<TMessage, IMessageBrokerContext, Task> receiverHandler,
                           Func<MessageBrokerContext, TransactionContext, Func<TMessage, IMessageBrokerContext, Task>, Task> inboundMessageHandler,
                           CancellationToken receiverTerminationToken);
    }
}
