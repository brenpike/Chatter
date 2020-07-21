using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Routes a brokered message to a receiver
    /// </summary>
    public interface IMessageDestinationRouter
    {
        /// <summary>
        /// Routes a brokered message to a receiver
        /// </summary>
        /// <param name="outboundBrokeredMessage">The outbound brokered message to be routed to a receiver</param>
        /// <param name="transactionContext">The transactional information to used while routing</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext);
    }

    /// <summary>
    /// Routes a brokered message to a receiver using context of type <typeparamref name="TDestinationRouterContext"/>
    /// </summary>
    /// <typeparam name="TDestinationRouterContext">The type of context containing information required to route a message</typeparam>
    public interface IMessageDestinationRouter<in TDestinationRouterContext> where TDestinationRouterContext : IContainDestinationToRouteContext
    {
        /// <summary>
        /// Routes a brokered message to a receiver
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be routed to a receiver</param>
        /// <param name="transactionContext">The transactional information to used while routing</param>
        /// <param name="destinationRouterContext">The contextual information required to successfully route the brokered message</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, TDestinationRouterContext destinationRouterContext);
    }
}
