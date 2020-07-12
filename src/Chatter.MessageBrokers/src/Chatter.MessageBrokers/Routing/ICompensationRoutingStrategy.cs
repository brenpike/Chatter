using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// The strategy used to route a compensating message for the failed received brokered message
    /// </summary>
    public interface ICompensationRoutingStrategy
    {
        /// <summary>
        /// Routes the compensation message for the failed received brokered message 
        /// </summary>
        /// <param name="inboundBrokeredMessage">The failed inbound brokered message that requires compensation</param>
        /// <param name="details">The details of the error that caused the compensation</param>
        /// <param name="description">A description of the error that caused the compensation</param>
        /// <param name="transactionContext">The transactional information to used while routing the compensation message</param>
        /// <param name="compensateContext">The contextual information required to compensate the message</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Compensate(InboundBrokeredMessage inboundBrokeredMessage, string details, string description, TransactionContext transactionContext, CompensateContext compensateContext);
    }
}
