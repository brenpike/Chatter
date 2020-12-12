using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using System.Collections.Generic;
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

        /// <summary>
        /// Sends a command to an external receiver via a message broker specified by <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to send.</typeparam>
        /// <param name="message">The message to be sent.</param>
        /// <param name="destinationPath">The destination path of the receiver that will receive this message.</param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Send<TMessage>(this IMessageHandlerContext messageHandlerContext, TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Send(message, destinationPath, messageHandlerContext?.GetTransactionContext(), CreateSendOptionsWithMessageContext(messageHandlerContext, options));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends a command to an external receiver via a message broker. Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of command to send.</typeparam>
        /// <param name="message">The command to be sent.</param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Send<TMessage>(this IMessageHandlerContext messageHandlerContext, TMessage message, SendOptions options = null) where TMessage : ICommand
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Send(message, messageHandlerContext?.GetTransactionContext(), CreateSendOptionsWithMessageContext(messageHandlerContext, options));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Publishes an event to all external receivers which are subscribed. Requires the publishing path to be specified by <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="destinationPath">The destination path that <paramref name="message"/> will be published to.</param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Publish<TMessage>(this IMessageHandlerContext messageHandlerContext, TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Publish(message, destinationPath, messageHandlerContext?.GetTransactionContext(), CreatePublishOptionsWithMessageContext(messageHandlerContext, options));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Publishes an event to all external receivers which are subscribed. Publisher path must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Publish<TMessage>(this IMessageHandlerContext messageHandlerContext, TMessage message, PublishOptions options = null) where TMessage : IEvent
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Publish(message, messageHandlerContext?.GetTransactionContext(), CreatePublishOptionsWithMessageContext(messageHandlerContext, options));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Publishes a batch of events to all external receivers which are subscribed. Publisher path must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="messages">The batch of events to be puublished.</param>
        /// <param name="options">The options to be used while publishing <paramref name="messages"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Publish<TMessage>(this IMessageHandlerContext messageHandlerContext, IEnumerable<TMessage> messages, PublishOptions options = null) where TMessage : IEvent
        {
            if (messageHandlerContext.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
            {
                return brokeredMessageDispatcher.Publish(messages, messageHandlerContext?.GetTransactionContext(), CreatePublishOptionsWithMessageContext(messageHandlerContext, options));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Forwards a message received from a message broker to a new destination
        /// </summary>
        /// <param name="forwardDestination">The destination to forward the message to.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
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

        private static SendOptions CreateSendOptionsWithMessageContext(IMessageHandlerContext messageHandlerContext, SendOptions options)
            => options ?? new SendOptions(messageHandlerContext.GetInboundBrokeredMessage().MessageContextImpl);

        private static PublishOptions CreatePublishOptionsWithMessageContext(IMessageHandlerContext messageHandlerContext, PublishOptions options)
            => options ?? new PublishOptions(messageHandlerContext.GetInboundBrokeredMessage().MessageContextImpl);

        private static T Get<T>(this IMessageHandlerContext messageHandlerContext)
        {
            messageHandlerContext.Container.TryGet<T>(out var context);
            return context;
        }
    }
}
