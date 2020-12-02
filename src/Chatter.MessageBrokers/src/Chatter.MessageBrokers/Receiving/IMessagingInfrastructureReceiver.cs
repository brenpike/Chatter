using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The message broker infrastructure used to receive messages
    /// </summary>
    public interface IMessagingInfrastructureReceiver : IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// Starts receiving messages via the message broker infrastructure
        /// </summary>
        Task StartReceiver(ReceiverOptions options,
                           Func<MessageBrokerContext, TransactionContext, Task> inboundMessageHandler);

        Task StopReceiver();
    }
}
