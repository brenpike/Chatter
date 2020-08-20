using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    public static class MessageHandlerContextExtensions
    {
        //TODO: can this be done? DispatchContext won't exist, so how will we get the InternalDispatcher???
        public static Task SendInternal<TMessage>(this IMessageHandlerContext mhc, TMessage message, TransactionContext transactionContext) where TMessage : ICommand
        {
            return Task.CompletedTask;
        }

        public static Task PublishInternal<TMessage>(this IMessageHandlerContext mhc, TMessage message, TransactionContext transactionContext) where TMessage : IEvent
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets contextual information about a message broker from message handler context
        /// </summary>
        /// <param name="messageHandlerContext">The message handler context</param>
        /// <returns>The message broker context</returns>
        public static MessageBrokerContext AsMessageBrokerContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext as MessageBrokerContext;
        }

        /// <summary>
        /// Gets contextual information about the transaction the message broker is a part of
        /// </summary>
        /// <param name="messageHandlerContext">The message handler context</param>
        /// <returns>The transaction context</returns>
        public static TransactionContext GetTransactionContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext.Get<TransactionContext>();
        }

        /// <summary>
        /// Gets contextual information about the reply destination
        /// </summary>
        /// <param name="messageHandlerContext">The message handler context</param>
        /// <returns>The reply destination context</returns>
        public static ReplyToRoutingContext GetReplyContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext.Get<ReplyToRoutingContext>();
        }

        /// <summary>
        /// Gets contextual information about the next destination
        /// </summary>
        /// <param name="messageHandlerContext">The message handler context</param>
        /// <returns>The next destination context</returns>
        public static RoutingContext GetNextDestinationContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext.Get<RoutingContext>();
        }

        /// <summary>
        /// Gets contextual information about the compensation destination
        /// </summary>
        /// <param name="messageHandlerContext">The message handler context</param>
        /// <returns>The compensation destination context</returns>
        public static CompensationRoutingContext GetCompensationContext(this IMessageHandlerContext messageHandlerContext)
        {
            return messageHandlerContext.Get<CompensationRoutingContext>();
        }

        /// <summary>
        /// Gets the inbound brokered message from the message handler context or null if the message handler context
        /// doesn't contain any contextual information about the message broker.
        /// </summary>
        /// <param name="messageHandlerContext"></param>
        /// <returns></returns>
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
