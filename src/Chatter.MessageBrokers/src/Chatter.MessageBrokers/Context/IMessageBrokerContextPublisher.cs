using Chatter.CQRS.Events;
using Chatter.CQRS;
using Chatter.MessageBrokers.Routing.Options;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    public interface IMessageBrokerContextPublisher
    {
        /// <summary>
        /// Publishes an event to all external receivers which are subscribed via the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If messaging infrastructure did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Publish overload (if available).
        /// Destination must be configured using <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="destinationPath">The destination path that <paramref name="message"/> will be published to.</param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent;

        /// <summary>
        /// Publishes an event to all external receivers which are subscribed via the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If messaging infrastructure did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Publish overload (if available).
        /// Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="message">The event to be publish.</param>
        /// <param name="options">The options to be used while publishing <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(TMessage message, PublishOptions options = null) where TMessage : IEvent;

        /// <summary>
        /// Publishes a batch of events to all external receivers which are subscribed via the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If messaging infrastructure did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Publish overload (if available).
        /// Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to publish.</typeparam>
        /// <param name="messages">The batch of events to be puublished.</param>
        /// <param name="options">The options to be used while publishing <paramref name="messages"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Publish<TMessage>(IEnumerable<TMessage> messages, PublishOptions options = null) where TMessage : IEvent;
    }
}
