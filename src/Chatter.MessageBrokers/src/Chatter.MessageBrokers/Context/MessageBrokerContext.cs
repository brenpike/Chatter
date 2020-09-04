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

        internal IBrokeredMessageDispatcher ExternalDispatcher { get; set; }

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

        //TODO: agree on guiding principals:
        //      1) MessageBrokerContext: 
        //          a) relaying context data to internal or external dispatchers
        //          b) providing methods that take TMessage instead of context specific params and outboundbrokeredmessage/inboundbrokeredmessage
        //      2) BrokeredMessageDispatcher:
        //          a) contains methods that require context from MessageBrokerContext as parameters along with TMessage
        //          b) simply forwards all routing requests to their respectived routers, i.e., IForwardRouter, compensate, replyTo, send, publish, etc.
        //          c) aggreagates all routing operations in a single class so it can be accessed by MessageBrokerContext
        //      3) Strongly typed routers (i.e., ReplyToRouter, ForwardingRouter, CompensateRouter) -> Need to create SendRouter and PublishRouter
        //          a) accept their own strongly typed IRoutingOptions
        //          b) apply their IRoutingOptions accordingly
        //          c) execute any router specific business logic (i.e., clearing replyto headers, setting compensate details/desc, etc.)
        //          d) ultimately calls IRoutesMessages.Route under the hood.
        //      4) IRouteMessages:
        //          a) core route implementation
        //          b) can accepted TMessage or OutboundBrokeredMessage
        //          c) ultimately the OutboundBrokeredMessage overload is called
        //          d) TMessage methods accept RoutingOptions for the purpose of creating OutboundBrokeredMessage.
        //             if called directly no IRoutingOption specific logic will be executed

        public Task Send<TMessage>(TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand 
            => this.ExternalDispatcher.Send(message, destinationPath, this.GetTransactionContext(), options);

        public Task Send<TMessage>(TMessage message, SendOptions options = null) where TMessage : ICommand 
            => this.ExternalDispatcher.Send(message, this.GetTransactionContext(), options);

        public Task Publish<TMessage>(TMessage message, PublishOptions options = null) where TMessage : IEvent 
            => this.ExternalDispatcher.Publish(message, this.GetTransactionContext(), options);

        public Task Publish<TMessage>(TMessage message, string destinationPath, PublishOptions options = null) where TMessage : IEvent 
            => this.ExternalDispatcher.Publish(message, destinationPath, this.GetTransactionContext(), options);
    }
}
