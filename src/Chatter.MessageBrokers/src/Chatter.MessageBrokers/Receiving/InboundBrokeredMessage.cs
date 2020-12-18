using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The message received by the <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/>
    /// </summary>
    public class InboundBrokeredMessage
    {
        internal IDictionary<string, object> MessageContextImpl { get; }

        internal InboundBrokeredMessage(string messageId, byte[] body, IDictionary<string, object> messageContext, string messageReceiverPath, IBrokeredMessageBodyConverter bodyConverter)
        {
            MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
            Body = body ?? throw new ArgumentNullException(nameof(body));
            MessageContextImpl = messageContext ?? new ConcurrentDictionary<string, object>();
            MessageReceiverPath = messageReceiverPath;
            BodyConverter = bodyConverter ?? throw new ArgumentNullException(nameof(bodyConverter));
            CorrelationId = GetMessageContextByKey<string>(MessageBrokers.MessageContext.CorrelationId);
            MessageContextImpl[MessageBrokers.MessageContext.ContentType] = bodyConverter.ContentType;
        }

        /// <summary>
        /// The message id of the received message
        /// </summary>
        public string MessageId { get; }
        /// <summary>
        /// The body of the received message
        /// </summary>
        public byte[] Body { get; }
        /// <summary>
        /// The application properties of the received message
        /// </summary>
        public IReadOnlyDictionary<string, object> MessageContext => (IReadOnlyDictionary<string, object>)MessageContextImpl;
        /// <summary>
        /// The name of the message receiver that recieved this message
        /// </summary>
        public string MessageReceiverPath { get; }
        /// <summary>
        /// The correlation id of the received message
        /// </summary>
        public string CorrelationId { get; }
        /// <summary>
        /// True if the inbound message has encountered an error while being received
        /// </summary>
        public bool IsError => GetMessageContextByKey<bool>(MessageBrokers.MessageContext.IsError);
        /// <summary>
        /// True if the inbound message has not encountered an error while being received
        /// </summary>
        public bool IsSuccess => !IsError;
        /// <summary>
        /// The receivers visited by the inbound message prior to the most recent message receiver
        /// </summary>
        public string Via => GetMessageContextByKey<string>(MessageBrokers.MessageContext.Via);
        internal IBrokeredMessageBodyConverter BodyConverter { get; }

        /// <summary>
        /// Gets a message of type <typeparamref name="TBody"/> from the message body
        /// </summary>
        /// <typeparam name="TBody">The type of the object stored as the message body</typeparam>
        /// <returns>The strongly typed message payload</returns>
        public TBody GetMessageFromBody<TBody>()
            => this.BodyConverter.Convert<TBody>(this.Body);

        internal InboundBrokeredMessage UpdateVia(string via)
        {
            var key = MessageBrokers.MessageContext.Via;
            if (MessageContextImpl.ContainsKey(key))
            {
                var currentVia = (string)MessageContext[key];
                if (!(string.IsNullOrWhiteSpace(via)))
                {
                    currentVia += "," + via;
                    MessageContextImpl[key] = currentVia;
                }
            }
            else
            {
                MessageContextImpl[key] = via;
            }
            return this;
        }

        private T GetMessageContextByKey<T>(string key)
        {
            if (MessageContextImpl.TryGetValue(key, out var output))
            {
                return (T)output;
            }
            else
            {
                return default;
            }
        }

        internal InboundBrokeredMessage WithFailureDetails(string failureDetails)
        {
            MessageContextImpl[MessageBrokers.MessageContext.FailureDetails] = failureDetails;
            return this;
        }

        internal InboundBrokeredMessage WithFailureDescription(string failureDescription)
        {
            MessageContextImpl[MessageBrokers.MessageContext.FailureDescription] = failureDescription;
            return this;
        }

        internal InboundBrokeredMessage ClearReplyToProperties()
        {
            MessageContextImpl.Remove(MessageBrokers.MessageContext.ReplyToAddress);
            MessageContextImpl.Remove(MessageBrokers.MessageContext.ReplyToGroupId);
            return this;
        }

        internal InboundBrokeredMessage WithRouteToSelfPath(string destinationPath)
        {
            MessageContextImpl[MessageBrokers.MessageContext.RouteToSelfPath] = destinationPath;
            return this;
        }
    }
}
