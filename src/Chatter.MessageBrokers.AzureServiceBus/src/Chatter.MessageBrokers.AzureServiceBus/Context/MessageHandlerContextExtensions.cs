using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.CQRS.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static IMessageBrokerContext AzureServiceBus(this IMessageHandlerContext context)
        {
            if (context is IMessageBrokerContext mbc)
            {
                mbc.BrokeredMessage?.UseMessagingInfrastructure(it => it.AzureServiceBus());
                return mbc;
            }

            return null;
        }

        /// <summary>
        /// Gets the durable per-session state for the Azure Service Bus session that delivered the message
        /// being handled. Only available while handling a message received through a session-enabled receiver.
        /// </summary>
        /// <param name="context">The context of the message handler</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/></param>
        /// <returns>The session state as <see cref="BinaryData"/>, or null if no session state has been set.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the message was not received through a session-enabled receiver.</exception>
        public static Task<BinaryData> GetSessionStateAsync(this IMessageHandlerContext context, CancellationToken cancellationToken = default)
            => GetHeldSessionReceiver(context).GetSessionStateAsync(cancellationToken);

        /// <summary>
        /// Sets the durable per-session state for the Azure Service Bus session that delivered the message
        /// being handled. Only available while handling a message received through a session-enabled receiver.
        /// </summary>
        /// <param name="context">The context of the message handler</param>
        /// <param name="sessionState">The session state to persist for the session.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <exception cref="InvalidOperationException">Thrown when the message was not received through a session-enabled receiver.</exception>
        public static Task SetSessionStateAsync(this IMessageHandlerContext context, BinaryData sessionState, CancellationToken cancellationToken = default)
            => GetHeldSessionReceiver(context).SetSessionStateAsync(sessionState, cancellationToken);

        /// <summary>
        /// Clears the durable per-session state for the Azure Service Bus session that delivered the message
        /// being handled. Only available while handling a message received through a session-enabled receiver.
        /// </summary>
        /// <param name="context">The context of the message handler</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <exception cref="InvalidOperationException">Thrown when the message was not received through a session-enabled receiver.</exception>
        public static Task ClearSessionStateAsync(this IMessageHandlerContext context, CancellationToken cancellationToken = default)
            => GetHeldSessionReceiver(context).SetSessionStateAsync(null, cancellationToken);

        // Resolves the held ServiceBusSessionReceiver that STEP-004 included in the transaction context
        // Container for session messages. Non-session messages have no session receiver in the Container,
        // so session-state access on them fails fast with a predictable misuse error rather than a no-op or NRE.
        private static ServiceBusSessionReceiver GetHeldSessionReceiver(IMessageHandlerContext context)
        {
            var transactionContext = context.GetTransactionContext();
            if (transactionContext != null
                && transactionContext.Container.TryGet<ServiceBusSessionReceiver>(out var sessionReceiver)
                && sessionReceiver != null)
            {
                return sessionReceiver;
            }

            throw new InvalidOperationException("Azure Service Bus session state is only available for session-enabled receivers.");
        }
    }
}
