using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Routes a brokered message to a receiver responsible for compensating a received message
    /// </summary>
    public interface ICompensateRouter : IMessageDestinationRouter<CompensateContext>
    {
        /// <summary>
        /// Routes a brokered message to a receiver responsible for compensating a received message
        /// </summary>
        /// <param name="compensateDestinationPath">The destination path for the receiver responsible for compensating a received message</param>
        /// <param name="inboundMessage">The inbound message that was unsuccesfully received and requires compensation</param>
        /// <param name="messageContext">The context that was received with <paramref name="inboundMessage"/></param>
        /// <param name="transactionContext">The transaction information that was received with <paramref name="inboundMessage"/></param>
        /// <param name="details">The details of the error that caused the compensation</param>
        /// <param name="description">The description of the error that caused the compensation</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Route(string compensateDestinationPath, InboundBrokeredMessage inboundMessage, MessageBrokerContext messageContext, TransactionContext transactionContext, string details, string description);
    }
}