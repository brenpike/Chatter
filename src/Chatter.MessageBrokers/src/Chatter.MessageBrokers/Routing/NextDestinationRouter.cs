using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public class NextDestinationRouter : INextDestinationRouter
    {
        private readonly MessageDestinationRouter<NextDestinationContext> _messageDestinationRouter;

        public NextDestinationRouter(IBrokeredMessageDispatcher messageBrokerMessageDispatcher)
        {
            _messageDestinationRouter = new MessageDestinationRouter<NextDestinationContext>(messageBrokerMessageDispatcher);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="inboundBrokeredMessage"></param>
        /// <param name="transactionContext"></param>
        /// <param name="destinationRouterContext"></param>
        /// <returns></returns>
        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, NextDestinationContext destinationRouterContext)
        {
            if (inboundBrokeredMessage is null)
            {
                throw new ArgumentNullException(nameof(inboundBrokeredMessage), $"An {typeof(InboundBrokeredMessage).Name} is required to be routed to the next destination.");
            }

            return _messageDestinationRouter.Route(inboundBrokeredMessage, transactionContext, destinationRouterContext);
        }
    }
}
