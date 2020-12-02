using Chatter.CQRS;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// An infrastructure agnostic receiver of brokered messages of type <typeparamref name="TMessage"/>
    /// </summary>
    /// <typeparam name="TMessage">The type of messages the brokered message receiver accepts</typeparam>
    public interface IBrokeredMessageReceiver<TMessage> : IAsyncDisposable, IDisposable, IReceiveMessages where TMessage : class, IMessage
    {
        /// <summary>
        /// Start listening to messages of type <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="receiverTerminationToken">The <see cref="CancellationToken"/> that cancels receiving messages of type <typeparamref name="TMessage"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task<IAsyncDisposable> StartReceiver(CancellationToken receiverTerminationToken);

        Task<IAsyncDisposable> StartReceiver();

        Task StopReceiver();
    }
}
