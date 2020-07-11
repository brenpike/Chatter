using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    internal sealed class MessageDestinationRouter<TDestinationRouterContext> : IMessageDestinationRouter<TDestinationRouterContext>
        where TDestinationRouterContext : IContainDestinationToRouteContext
    {
        private readonly IBrokeredMessageDispatcher _messageDispatcher;

        public MessageDestinationRouter(IBrokeredMessageDispatcher messageDispatcher)
        {
            _messageDispatcher = messageDispatcher ?? throw new ArgumentNullException(nameof(messageDispatcher));
        }

        /// <summary>
        /// Routes a brokered message to a receiver using context of type <typeparamref name="TDestinationRouterContext"/> by dispatching a brokered message
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be routed to the destination receiver</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <param name="destinationRouterContext">The contextual information required to route the message to the destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, TDestinationRouterContext destinationRouterContext)
        {
            if (destinationRouterContext is null)
            {
                return Task.CompletedTask;
            }

            return _messageDispatcher.Dispatch(destinationRouterContext.CreateDestinationMessage(inboundBrokeredMessage), transactionContext);
        }
    }
}