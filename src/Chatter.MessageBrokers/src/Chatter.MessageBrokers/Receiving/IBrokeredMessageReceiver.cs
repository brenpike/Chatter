using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    public interface IBrokeredMessageReceiver<TMessage> : IReceiveMessages, IDescribeBrokeredMessage where TMessage : class, IMessage
    {
        void StartReceiver();
        Task StartReceiver(Func<TMessage, IMessageBrokerContext, Task> receiverHandler, CancellationToken receiverTerminationToken);
        Task StartReceiver(CancellationToken receiverTerminationToken);
        void StopReceiver();
    }
}
