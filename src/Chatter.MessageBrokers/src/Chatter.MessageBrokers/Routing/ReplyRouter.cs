using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public class ReplyRouter : IReplyRouter
    {
        private readonly MessageDestinationRouter<ReplyDestinationContext> _messageDestinationRouter;

        public ReplyRouter(IBrokeredMessageDispatcher messageBrokerMessageDispatcher)
        {
            _messageDestinationRouter = new MessageDestinationRouter<ReplyDestinationContext>(messageBrokerMessageDispatcher);
        }

        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, ReplyDestinationContext destinationRouterContext)
        {
            if (inboundBrokeredMessage is null)
            {
                throw new ArgumentNullException(nameof(inboundBrokeredMessage), $"An {typeof(InboundBrokeredMessage).Name} is required to be routed to the reply destination.");
            }

            return _messageDestinationRouter.Route(inboundBrokeredMessage, transactionContext, destinationRouterContext);
        }
    }
}
