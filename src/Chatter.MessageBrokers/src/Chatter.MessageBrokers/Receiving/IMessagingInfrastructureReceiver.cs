using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The message broker infrastructure used to receive messages
    /// </summary>
    /// <typeparam name="TMessage">The type of message the message broker infrastructure receives</typeparam>
    public interface IMessagingInfrastructureReceiver<TMessage> where TMessage : class, IMessage
    {
        /// <summary>
        /// Starts receiving messages via the message broker infrastructure
        /// </summary>
        /// <param name="inboundMessageHandler">The delegate to be invoked when a message is received by the message broker infrastructure</param>
        /// <param name="receiverTerminationToken">The <see cref="CancellationToken"/> used to stop the message broker infrastructure from receiving messages</param>
        void StartReceiver(Func<MessageBrokerContext, TransactionContext, Task> inboundMessageHandler,
                           CancellationToken receiverTerminationToken);
    }
}
