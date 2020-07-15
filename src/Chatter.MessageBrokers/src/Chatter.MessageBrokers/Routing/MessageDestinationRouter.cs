using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public sealed class MessageDestinationRouter<TDestinationRouterContext> : IMessageDestinationRouter, IMessageDestinationRouter<TDestinationRouterContext>
        where TDestinationRouterContext : IContainDestinationToRouteContext
    {
        private readonly IBrokeredMessageInfrastructureDispatcher _messageDispatcher;
        private readonly IReliableBrokeredMessageProcessor _brokeredMessageOutbox;

        public MessageDestinationRouter(IBrokeredMessageInfrastructureDispatcher messageDispatcher, IReliableBrokeredMessageProcessor brokeredMessageOutbox)
        {
            _messageDispatcher = messageDispatcher ?? throw new ArgumentNullException(nameof(messageDispatcher));
            _brokeredMessageOutbox = brokeredMessageOutbox ?? throw new ArgumentNullException(nameof(brokeredMessageOutbox));
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

        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            if (_brokeredMessageOutbox.IsReliableMesageProcessingEnabled)
            {
                return _brokeredMessageOutbox.SendToOutbox(outboundBrokeredMessage, transactionContext);
            }

            return _messageDispatcher.Dispatch(outboundBrokeredMessage, transactionContext);
        }
    }
}