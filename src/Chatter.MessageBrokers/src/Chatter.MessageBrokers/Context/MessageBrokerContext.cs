using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Used to pass contextual information of a <see cref="BrokeredMessageReceiver{TMessage}"/> to a <see cref="IMessageHandler{TMessage}"/>
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
        public MessageBrokerContext(string messageId, byte[] body, IDictionary<string, object> applicationProperties, string messageReceiverPath, IBrokeredMessageBodyConverter bodyConverter)
        {
            this.BrokeredMessage = new InboundBrokeredMessage(messageId, body, applicationProperties, messageReceiverPath, bodyConverter);
        }

        public MessageBrokerContext(InboundBrokeredMessage brokeredMessage)
        {
            this.BrokeredMessage = brokeredMessage;
        }

        /// <summary>
        /// The received message
        /// </summary>
        public InboundBrokeredMessage BrokeredMessage { get; private set; }

        internal IBrokeredMessageDispatcher ExternalDispatcher
        {
            get
            {
                if (this.DispatchContext.ExternalDispatcher is null)
                {
                    throw new ArgumentNullException(nameof(this.DispatchContext.InternalDispatcher), $"No dispatcher was found to facilitate external messaging. Unable to route messages.");
                }

                return this.DispatchContext.ExternalDispatcher;
            }
        }

        internal IMessageDispatcher InternalDispatcher
        {
            get
            {
                if (this.DispatchContext.InternalDispatcher is null)
                {
                    throw new ArgumentNullException(nameof(this.DispatchContext.InternalDispatcher), $"No dispatcher was found to facilitate internal messaging. Unable to route messages.");
                }

                return this.DispatchContext.InternalDispatcher;
            }
        }

        internal DispatchContext DispatchContext
        {
            get
            {
                if (this.Container.TryGet<DispatchContext>(out var dispatchContext))
                {
                    return dispatchContext;
                }
                else
                {
                    throw new ArgumentNullException(nameof(dispatchContext), $"No dispatch context was found. Unable to route messages.");
                }
            }
        }

        /// <summary>
        /// Adds contextual error information to the message broker context
        /// </summary>
        /// <param name="errorContext"></param>
        public void SetFailure(ErrorContext errorContext)
        {
            this.Container.Include(errorContext);
            this.BrokeredMessage.SetFailure();
            this.BrokeredMessage.WithFailureDetails(errorContext.ErrorDetails);
            this.BrokeredMessage.WithFailureDescription(errorContext.ErrorDescription);
        }

        public Task Send<TMessage>(TMessage message, string destinationPath) where TMessage : ICommand
        {
            return this.ExternalDispatcher.Send(message, destinationPath, this.GetTransactionContext());
        }

        public Task Send<TMessage>(TMessage message) where TMessage : ICommand
        {
            return this.ExternalDispatcher.Send(message, this.GetTransactionContext());
        }

        public Task Publish<TMessage>(TMessage message) where TMessage : IEvent
        {
            return this.ExternalDispatcher.Publish(message, this.GetTransactionContext());
        }

        public Task Publish<TMessage>(IEnumerable<TMessage> messages) where TMessage : IEvent
        {
            return this.ExternalDispatcher.Publish(messages, this.GetTransactionContext());
        }

        public Task Publish<TMessage>(TMessage message, string destinationPath) where TMessage : IEvent
        {
            return this.ExternalDispatcher.Publish(message, destinationPath, this.GetTransactionContext());
        }

        public Task ReplyTo()
        {
            if (!this.Container.TryGet<ReplyToRoutingContext>(out var routingContext))
            {
                //log
                return Task.CompletedTask;
            }

            return this.ExternalDispatcher.ReplyTo(this.BrokeredMessage, routingContext, this.GetTransactionContext());
        }

        public Task ReplyToRequester<TMessage>(TMessage message) where TMessage : ICommand
        {
            //TODO: finish
            throw new System.NotImplementedException();
        }

        public Task Forward<TRoutingContext>() where TRoutingContext : IContainRoutingContext
        {
            if (!this.Container.TryGet<TRoutingContext>(out var routingContext))
            {
                //log
                return Task.CompletedTask;
            }           

            return this.ExternalDispatcher.Forward(this.BrokeredMessage, routingContext, this.GetTransactionContext());
        }

        public Task Compensate()
        {
            if (!this.Container.TryGet<CompensationRoutingContext>(out var routingContext))
            {
                //log
                return Task.CompletedTask;
            }

            return this.ExternalDispatcher.Compensate(this.BrokeredMessage, routingContext, this.GetTransactionContext());
        }

        public Task Compensate<TMessage>(TMessage message, string compensationDescription, string compensationDetails) where TMessage : ICommand
        {
            //TODO: implement
            throw new NotImplementedException();
        }

        public Task Compensate<TMessage>(TMessage message, string destinationPath, string compensationDescription, string compensationDetails) where TMessage : ICommand
        {
            //TODO: implement
            throw new NotImplementedException();
        }
    }
}
