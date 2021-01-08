using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessagePublisher
    {
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
        /// Publishes an event to all external receivers which are subscribed. Requires the publishing path to be specified by <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="destinationPath">The destination path that <paramref name="message"/> will be published to.</param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(TMessage message, string destinationPath, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent;
        /// <summary>
        /// Publishes an event to all external receivers which are subscribed. Publisher path must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent;
        /// <summary>
        /// Publishes a batch of events to all external receivers which are subscribed. Publisher path must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="messages">The batch of events to be puublished.</param>
        /// <param name="options">The options to be used while publishing <paramref name="messages"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(IEnumerable<TMessage> messages, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent;
    }
}
