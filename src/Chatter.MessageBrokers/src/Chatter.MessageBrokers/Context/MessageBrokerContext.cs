using Chatter.CQRS.Context;
using Chatter.CQRS;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using System.Collections.Generic;

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
        ///<inheritdoc/>
        public INextDestinationRouter NextDestinationRouter { get; internal set; }
        ///<inheritdoc/>
        public IReplyRouter ReplyRouter { get; internal set; }
        ///<inheritdoc/>
        public ICompensateRouter CompensateRouter { get; internal set; }

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
    }
}
