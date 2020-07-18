using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public sealed class MessageDestinationRouter<TDestinationRouterContext> : IMessageDestinationRouter, IMessageDestinationRouter<TDestinationRouterContext>
        where TDestinationRouterContext : IContainDestinationToRouteContext
    {
        private readonly IBrokeredMessageInfrastructureDispatcher _brokeredMessageInfrastructureDispatcher;

        public MessageDestinationRouter(IBrokeredMessageInfrastructureDispatcher brokeredMessageInfrastructureDispatcher)
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
        /// Routes a brokered message to a receiver by routing an <see cref="OutboundBrokeredMessage"/> to the message broker infrastructure.
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
    }
}