using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static bool TryGetExternalDispatcher(this IMessageHandlerContext context, out IBrokeredMessageDispatcher brokeredMessageDispatcher)
        {
            if (context.Container.TryGet<IExternalDispatcher>(out var ed))
            {
                if (ed is IBrokeredMessageDispatcher dispatcher)
                {
                    brokeredMessageDispatcher = dispatcher;
                    return true;
                }
            }

            brokeredMessageDispatcher = null;
            return false;
        }

        public static Task Send<TMessage>(this IMessageHandlerContext messageHandlerContext, TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Send(message, destinationPath, messageHandlerContext?.GetTransactionContext(), options);
            }

            return Task.CompletedTask;
        }

        public static Task Send<TMessage>(this IMessageHandlerContext messageHandlerContext, TMessage message, SendOptions options = null) where TMessage : ICommand
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Send(message, messageHandlerContext?.GetTransactionContext(), options);
            }

            return Task.CompletedTask;
        }

        public static Task Publish<TMessage>(this IMessageHandlerContext messageHandlerContext, TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Publish(message, destinationPath, messageHandlerContext?.GetTransactionContext(), options);
            }

            return Task.CompletedTask;
        }

        public static Task Publish<TMessage>(this IMessageHandlerContext messageHandlerContext, TMessage message, PublishOptions options = null) where TMessage : IEvent
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Publish(message, messageHandlerContext?.GetTransactionContext(), options);
            }

            return Task.CompletedTask;
        }

        public static Task Forward(this IMessageHandlerContext context, string forwardDestination)
        {
            if (context.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Forward(context.GetInboundBrokeredMessage(), forwardDestination, context?.GetTransactionContext());
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets contextual information about a message broker from message handler context
        /// </summary>
        /// <param name="messageHandlerContext">The message handler context</param>
        /// <returns>The message broker context</returns>
        public static MessageBrokerContext AsMessageBrokerContext(this IMessageHandlerContext messageHandlerContext)
            => messageHandlerContext as MessageBrokerContext;

        /// <summary>
        /// Gets contextual information about the transaction the message broker is a part of
        /// </summary>
        /// <param name="messageHandlerContext">The message handler context</param>
        /// <returns>The transaction context</returns>
        public static TransactionContext GetTransactionContext(this IMessageHandlerContext messageHandlerContext)
            => messageHandlerContext.Get<TransactionContext>();

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
