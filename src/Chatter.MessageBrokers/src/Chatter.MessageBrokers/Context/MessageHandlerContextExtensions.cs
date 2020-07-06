using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;

namespace Chatter.MessageBrokers.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static MessageBrokerContext AsMessageBrokerContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext as MessageBrokerContext;
        }

        public static TransactionContext GetTransactionContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext.Get<TransactionContext>();
        }

        public static ReplyDestinationContext GetReplyContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext.Get<ReplyDestinationContext>();
        }

        public static NextDestinationContext GetNextDestinationContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext.Get<NextDestinationContext>();
        }

        public static CompensateContext GetCompensationContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext.Get<CompensateContext>();
        }

        public static InboundBrokeredMessage GetInboundBrokeredMessage(this IMessageHandlerContext messageHandlerContext)
        {
            if (messageHandlerContext is IMessageBrokerContext brokeredContext)
            {
                return brokeredContext.BrokeredMessage;
            }

            return default;
        }

        private static T Get<T>(this IMessageHandlerContext messageHandlerContext)
        {
            messageHandlerContext.Container.TryGet<T>(out var context);
            return context;
        }
    }
}
