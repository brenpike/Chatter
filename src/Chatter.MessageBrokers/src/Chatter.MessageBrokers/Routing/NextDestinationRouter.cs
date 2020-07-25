using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public class NextDestinationRouter : INextDestinationRouter
    {
        private readonly IRouteMessages<NextDestinationRoutingContext> _messageDestinationRouter;

        public NextDestinationRouter(IRouteMessages<NextDestinationRoutingContext> messageDestinationRouter)
        {
            _messageDestinationRouter = messageDestinationRouter;
        }

        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, NextDestinationRoutingContext destinationRouterContext)
        {
            if (inboundBrokeredMessage is null)
            {
                throw new ArgumentNullException(nameof(inboundBrokeredMessage), $"An {typeof(InboundBrokeredMessage).Name} is required to be routed to the next destination.");
            }

            return _messageDestinationRouter.Route(inboundBrokeredMessage, transactionContext, destinationRouterContext);
        }
    }
}
