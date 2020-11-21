using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageDispatcher : IExternalDispatcher
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
        /// Publishes an event to all external receivers which are subscribed. Requires the publishing path to be specified by <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="destinationPath">The destination path that <paramref name="message"/> will be published to.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent;
        /// <summary>
        /// Publishes an event to all external receivers which are subscribed. Publisher path must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent;
        /// <summary>
        /// Publishes a batch of events to all external receivers which are subscribed. Publisher path must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="messages">The batch of events to be puublished.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <param name="options">The options to be used while publishing <paramref name="messages"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(IEnumerable<TMessage> messages, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent;
        /// <summary>
        /// Forwards a message received from a message broker to a new destination
        /// </summary>
        /// <param name="inboundBrokeredMessage">The message received from a message broker. An <see cref="InboundBrokeredMessage"/> can be found in <see cref="MessageBrokerContext"/></param>
        /// <param name="forwardDestination">The destination to forward the message to.</param>
        /// <param name="transactionContext">Contextual transaction information that can be retrieved from the <see cref="IMessageBrokerContext"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext);
    }
}
