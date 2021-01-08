using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageForwarder
    {
        /// <summary>
        /// Forwards a message received from a message broker to a new destination
        /// </summary>
        /// <param name="inboundBrokeredMessage">The message received from a message broker. An <see cref="InboundBrokeredMessage"/> can be found in <see cref="MessageBrokerContext"/></param>
        /// <param name="forwardDestination">The destination to forward the message to.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext);

        /// <summary>
        /// Forwards a message received from a message broker to a new destination
        /// </summary>
        /// <param name="forwardDestination">The destination to forward the message to.</param>
        /// <param name="context">The <see cref="IMessageBrokerContext"/> containing the <see cref="InboundBrokeredMessage"/> to be forwarded and other contextual information.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Forward(string forwardDestination, IMessageBrokerContext context);
    }
}
