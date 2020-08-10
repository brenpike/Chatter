using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Forwards an brokered message to a receiver
    /// </summary>
    public interface IForwardMessages
    {
        /// <summary>
        /// Forwards an inbound brokered message to a destination
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be forwarded to a receiver</param>
        /// <param name="forwardDestination">The destination path to forward the inbound brokered message to</param>
        /// <param name="transactionContext">The transactional information to use while routing</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Route(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext);
    }
}
