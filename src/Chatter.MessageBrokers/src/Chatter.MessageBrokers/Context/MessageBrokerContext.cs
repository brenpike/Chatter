using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using System.Collections.Generic;
using System.Threading;

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
        {
            this.BrokeredMessage = new InboundBrokeredMessage(messageId, body, applicationProperties, messageReceiverPath, bodyConverter);
            this.ReceiverCancellationToken = receiverCancellationToken;
        }

        /// <summary>
        /// The received message
        /// </summary>
        public InboundBrokeredMessage BrokeredMessage { get; private set; }

        public CancellationToken ReceiverCancellationToken { get; private set; }
    }
}
