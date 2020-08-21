using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public interface IReplyRouter
    {
        /// <summary>
        /// Routes a brokered message to a brokered message receiver using
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be routed to the 'reply to' destination</param>
        /// <param name="transactionContext">The transaction information that was received with <paramref name="inboundBrokeredMessage"/></param>
        /// <param name="destinationRouterContext">The <see cref="ReplyToRoutingContext"/> containing contextual information describing 'reply to' destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, ReplyToRoutingContext destinationRouterContext);
    }
}
