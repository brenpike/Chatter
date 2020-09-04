using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageDispatcher : IExternalDispatcher
    {
        /// <summary>
        /// Sends a command to an external receiver specified by <paramref name="destinationPath"/>./>
        /// </summary>
        /// <typeparam name="TMessage">The type of message to send.</typeparam>
        /// <param name="message">The message to be sent.</param>
        /// <param name="destinationPath">The destination path of the receiver that will receive this message.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Send<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand;
        /// <summary>
        /// Sends a command to an external receiver. Destination must be configured using <see cref="BrokeredMessageAttribute"/>./>
        /// </summary>
        /// <typeparam name="TMessage">The type of command to send.</typeparam>
        /// <param name="message">The command to be sent.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand;
        /// <summary>
        /// Publishes an event to all external receivers which are subscribed. Requires the publishing path to be specified by <paramref name="destinationPath"/>./>
        /// </summary>
        /// <typeparam name="TMessage">The type of command to send.</typeparam>
        /// <param name="message">The command to be sent.</param>
        /// <param name="destinationPath">The destination path that <paramref name="message"/> will be published to.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent;
        /// <summary>
        /// Publishes an event to all external receivers which are subscribed. Publisher path must be configured using <see cref="BrokeredMessageAttribute"/>./>
        /// </summary>
        /// <typeparam name="TMessage">The type of command to send.</typeparam>
        /// <param name="message">The command to be sent.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent;
    }
}
