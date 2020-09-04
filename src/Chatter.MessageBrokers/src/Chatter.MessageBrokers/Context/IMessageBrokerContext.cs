using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Used to pass contextual information of a <see cref="BrokeredMessageReceiver{TMessage}"/> to a <see cref="IMessageHandler{TMessage}"/>
    /// </summary>
    public interface IMessageBrokerContext : IMessageHandlerContext
    {
        /// <summary>
        /// The message received by a <see cref="BrokeredMessageReceiver{TMessage}"/>
        /// </summary>
        InboundBrokeredMessage BrokeredMessage { get; }
    }
}
