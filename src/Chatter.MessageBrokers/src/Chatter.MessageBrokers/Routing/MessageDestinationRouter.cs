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