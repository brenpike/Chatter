using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageSender
    {
        /// <summary>
        /// Sends a command to an external receiver via a message broker specified by <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to send.</typeparam>
        /// <param name="message">The message to be sent.</param>
        /// <param name="destinationPath">The destination path of the receiver that will receive this message.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Send<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand;
        /// <summary>
        /// Sends a command to an external receiver via a message broker. Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of command to send.</typeparam>
        /// <param name="message">The command to be sent.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand;
        /// <summary>
        /// Sends a command to an external receiver via a message broker specified by <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to send.</typeparam>
        /// <param name="message">The message to be sent.</param>
        /// <param name="destinationPath">The destination path of the receiver that will receive this message.</param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Send<TMessage>(TMessage message, string destinationPath, IMessageHandlerContext messageHandlerContext, SendOptions options = null) where TMessage : ICommand;
        /// <summary>
        /// Sends a command to an external receiver via a message broker. Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of command to send.</typeparam>
        /// <param name="message">The command to be sent.</param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Send<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, SendOptions options = null) where TMessage : ICommand;
    }
}
