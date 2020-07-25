using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public class ReplyRouter : IReplyRouter
    {
        private readonly IRouteMessages<ReplyRoutingContext> _messageDestinationRouter;

        public ReplyRouter(IRouteMessages<ReplyRoutingContext> messageDestinationRouter)
        {
            _messageDestinationRouter = messageDestinationRouter;
        }

        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, ReplyRoutingContext destinationRouterContext)
        {
            if (inboundBrokeredMessage is null)
            {
                throw new ArgumentNullException(nameof(inboundBrokeredMessage), $"An {typeof(InboundBrokeredMessage).Name} is required to be routed to the reply destination.");
            }

            return _messageDestinationRouter.Route(inboundBrokeredMessage, transactionContext, destinationRouterContext);
        }
    }
}
