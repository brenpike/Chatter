using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.CQRS.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static bool TryGetBrokeredMessageDispatcher(this IMessageHandlerContext context, out IBrokeredMessageDispatcher brokeredMessageDispatcher)
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
        /// Returns an <see cref="IInMemoryDispatcher"/> used to dispatch messages within the same process
        /// </summary>
        /// <param name="context">The context of the message handler</param>
        /// <returns>An <see cref="IInMemoryDispatcher"/></returns>
        public static IInMemoryDispatcher InMemory(this IMessageHandlerContext context)
            => new InMemoryDispatcher(context);

        /// <summary>
        /// Sends a command to the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If <see cref="BrokeredMessageReceiver{TMessage}"/> did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Send overload (if available).
        /// Destination must be configured using <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to send.</typeparam>
        /// <param name="message">The message to be sent.</param>
        /// <param name="destinationPath">The destination path of the receiver that will receive this message.</param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Send<TMessage>(this IMessageHandlerContext context, TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand
        {
            if (context.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Send(message, destinationPath, context, options);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends a command to the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If <see cref="BrokeredMessageReceiver{TMessage}"/> did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Send overload (if available).
        /// Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of command to send.</typeparam>
        /// <param name="message">The command to be sent.</param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Send<TMessage>(this IMessageHandlerContext context, TMessage message, SendOptions options = null) where TMessage : ICommand
        {
            if (context.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Send(message, context, options);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Publishes an event to all external receivers which are subscribed via the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If <see cref="BrokeredMessageReceiver{TMessage}"/> did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Publish overload (if available).
        /// Destination must be configured using <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="destinationPath">The destination path that <paramref name="message"/> will be published to.</param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Publish<TMessage>(this IMessageHandlerContext context, TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent
        {
            if (context.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Publish(message, destinationPath, context, options);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Publishes an event to all external receivers which are subscribed via the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If <see cref="BrokeredMessageReceiver{TMessage}"/> did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Publish overload (if available).
        /// Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Publish<TMessage>(this IMessageHandlerContext context, TMessage message, PublishOptions options = null) where TMessage : IEvent
        {
            if (context.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Publish(message, context, options);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Publishes a batch of events to all external receivers which are subscribed via the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If <see cref="BrokeredMessageReceiver{TMessage}"/> did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Publish overload (if available).
        /// Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="messages">The batch of events to be puublished.</param>
        /// <param name="options">The options to be used while publishing <paramref name="messages"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public static Task Publish<TMessage>(this IMessageHandlerContext context, IEnumerable<TMessage> messages, PublishOptions options = null) where TMessage : IEvent
        {
            if (context.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Publish(messages, context, options);
            }

            return Task.CompletedTask;
        }

        ///// <summary>
        ///// Gets contextual information about a message broker from message handler context
        ///// </summary>
        ///// <param name="messageHandlerContext">The message handler context</param>
        ///// <returns>The message broker context</returns>
        //public static MessageBrokerContext AsMessageBrokerContext(this IMessageHandlerContext messageHandlerContext)
        //    => messageHandlerContext as MessageBrokerContext;

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
