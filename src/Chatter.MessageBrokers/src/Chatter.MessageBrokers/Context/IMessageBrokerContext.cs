using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using System.Threading;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Used to pass contextual information of a <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> to a <see cref="IMessageHandler{TMessage}"/>
    /// </summary>
    public interface IMessageBrokerContext : IMessageHandlerContext
    {
        /// <summary>
        /// The message received by a <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/>
        /// </summary>
        InboundBrokeredMessage BrokeredMessage { get; }

        /// <summary>
        /// A cancellation token to cancel the receiver of the currently received <see cref="BrokeredMessage"/>
        /// </summary>
        CancellationToken ReceiverCancellationToken { get; }
    }
}
