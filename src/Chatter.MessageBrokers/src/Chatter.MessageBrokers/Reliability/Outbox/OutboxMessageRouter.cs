using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public sealed class OutboxMessageRouter<TDestinationRouterContext> : IRouteMessages, IRouteMessages<TDestinationRouterContext>
        where TDestinationRouterContext : IContainRoutingContext
    {
        private readonly ITransactionalBrokeredMessageOutbox _brokeredMessageOutboxDispatcher;

        public OutboxMessageRouter(ITransactionalBrokeredMessageOutbox brokeredMessageOutboxDispatcher)
        {
            _brokeredMessageOutboxDispatcher = brokeredMessageOutboxDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageOutboxDispatcher));
        }

        /// <summary>
        /// Routes a brokered message to a receiver using context of type <typeparamref name="TDestinationRouterContext"/> by 
        /// ending a message to the brokered message outbox.
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be routed to the destination receiver</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <param name="destinationRouterContext">The contextual information required to route the message to the destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, TDestinationRouterContext destinationRouterContext)
        {
            if (inboundBrokeredMessage is null)
            {
                throw new ArgumentNullException(nameof(inboundBrokeredMessage), $"An {typeof(InboundBrokeredMessage).Name} is required to be routed to the destination.");
            }

            if (destinationRouterContext is null)
            {
                return Task.CompletedTask;
            }

            var outboundMessage = OutboundBrokeredMessage.Forward(inboundBrokeredMessage, destinationRouterContext.DestinationPath);
            return Route(outboundMessage, transactionContext);
        }

        /// <summary>
        /// Routes an <see cref="OutboundBrokeredMessage"/> to a receiver via the brokered message outbox.
        /// </summary>
        /// <param name="outboundBrokeredMessage">The outbound brokered message to be routed to the destination receiver</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            return _brokeredMessageOutboxDispatcher.SendToOutbox(outboundBrokeredMessage, transactionContext);
        }

        /// <summary>
        /// Routes a batch of <see cref="OutboundBrokeredMessage"/> to their receivers via the brokered message outbox.
        /// </summary>
        /// <param name="outboundBrokeredMessages">The outbound brokered messages to be routed to the destination receivers</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(IList<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
        {
            return _brokeredMessageOutboxDispatcher.SendToOutbox(outboundBrokeredMessages, transactionContext);
        }
    }
}
