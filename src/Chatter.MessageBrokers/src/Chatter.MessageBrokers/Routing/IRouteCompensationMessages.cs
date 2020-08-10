using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public interface IRouteCompensationMessages
    {
        /// <summary>
        /// Routes a brokered message to a brokered message receiver responsible for compensating a received message
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be routed to the compensation destination</param>
        /// <param name="transactionContext">The transaction information that was received with <paramref name="inboundBrokeredMessage"/></param>
        /// <param name="destinationRouterContext">The <see cref="CompensationRoutingContext"/> containing contextual information describing the compensating action</param>
        /// <exception cref="CompensationRoutingException">An exception containing contextual information describing the failure during compensation and routing details</exception>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, CompensationRoutingContext destinationRouterContext);
    }
}
