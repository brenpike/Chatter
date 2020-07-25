using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public sealed class MessageRouter<TDestinationRouterContext> : IRouteMessages, IRouteMessages<TDestinationRouterContext>
        where TDestinationRouterContext : IContainRoutingContext
    {
        private readonly IBrokeredMessageInfrastructureDispatcher _brokeredMessageInfrastructureDispatcher;

        public MessageRouter(IBrokeredMessageInfrastructureDispatcher brokeredMessageInfrastructureDispatcher)
        {
            _brokeredMessageInfrastructureDispatcher = brokeredMessageInfrastructureDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageInfrastructureDispatcher));
        }

        /// <summary>
        /// Routes a brokered message to a receiver using context of type <typeparamref name="TDestinationRouterContext"/> by 
        /// dispatching a brokered message to messaging infrastructure OR sending a message to an outbox if brokered message outbox
        /// is enabled.
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

            var outboundMessage = destinationRouterContext.CreateDestinationMessage(inboundBrokeredMessage);
            return Route(outboundMessage, transactionContext);
        }

        /// <summary>
        /// Routes an <see cref="OutboundBrokeredMessage"/> to the receiver via the message broker infrastructure.
        /// </summary>
        /// <param name="outboundBrokeredMessage">The outbound brokered message to be routed to the destination receiver</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            if (string.IsNullOrWhiteSpace(outboundBrokeredMessage.Destination))
            {
                throw new ArgumentNullException(nameof(outboundBrokeredMessage.Destination), $"Unable to route message with no destination path specified");
            }

            return _brokeredMessageInfrastructureDispatcher.Dispatch(outboundBrokeredMessage, transactionContext);
        }

        /// <summary>
        /// Routes a batch of <see cref="OutboundBrokeredMessage"/> to their receivers via the message broker infrastructure.
        /// </summary>
        /// <param name="outboundBrokeredMessages">The outbound brokered messages to be routed to the destination receivers</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(IList<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
        {
            return _brokeredMessageInfrastructureDispatcher.Dispatch(outboundBrokeredMessages, transactionContext);
        }
    }
}