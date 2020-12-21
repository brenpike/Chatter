using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Used to pass contextual information of a <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> to a <see cref="IMessageHandler{TMessage}"/>
    /// </summary>
    public sealed class MessageBrokerContext : MessageHandlerContext, IMessageBrokerContext
    {
        /// <summary>
        /// Creates an object containing context about the message received by the message broker
        /// </summary>
        /// <param name="messageId">The id of the received message</param>
        /// <param name="body">The body of the received message</param>
        /// <param name="applicationProperties">The application properties of the received message</param>
        /// <param name="messageReceiverPath">The message receiver path</param>
        /// <param name="bodyConverter">Used to convert the message body to a strongly typed object</param>
        public MessageBrokerContext(string messageId, byte[] body, IDictionary<string, object> applicationProperties, string messageReceiverPath, CancellationToken receiverCancellationToken, IBrokeredMessageBodyConverter bodyConverter)
            : base(receiverCancellationToken) 
            => BrokeredMessage = new InboundBrokeredMessage(messageId, body, applicationProperties, messageReceiverPath, bodyConverter);

        /// <summary>
        /// The received message
        /// </summary>
        public InboundBrokeredMessage BrokeredMessage { get; private set; }

        /// <inheritdoc/>
        public Task Forward(string forwardDestination)
        {
            if (this.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Forward(forwardDestination, this);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent
        {
            if (this.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Publish(message, destinationPath, this, options);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, PublishOptions options = null) where TMessage : IEvent
        {
            if (this.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Publish(message, this, options);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task Publish<TMessage>(IEnumerable<TMessage> messages, PublishOptions options = null) where TMessage : IEvent
        {
            if (this.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Publish(messages, this, options);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand
        {
            if (this.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Send(message, destinationPath, this, options);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, SendOptions options = null) where TMessage : ICommand
        {
            if (this.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Send(message, this, options);
            }

            return Task.CompletedTask;
        }
    }
}
