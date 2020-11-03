using Chatter.MessageBrokers.Receiving;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Sending
{
    public class OutboundBrokeredMessage
    {
        private readonly IBrokeredMessageBodyConverter _bodyConverter;

        public OutboundBrokeredMessage(string messageId, byte[] body, IDictionary<string, object> messageContext, string destination, IBrokeredMessageBodyConverter bodyConverter)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentException($"A destination is required for an {typeof(OutboundBrokeredMessage).Name}.", nameof(destination));
            }

            MessageContext = messageContext ?? new ConcurrentDictionary<string, object>();

            MessageId = messageId;
            Body = body ?? throw new ArgumentNullException(nameof(body));
            Destination = destination;
            _bodyConverter = bodyConverter ?? throw new ArgumentNullException(nameof(bodyConverter));
            MessageContext[MessageBrokers.MessageContext.ContentType] = _bodyConverter.ContentType;

            if (string.IsNullOrWhiteSpace(GetCorrelationId()))
            {
                WithCorrelationId(Guid.NewGuid().ToString());
            }
        }

        public OutboundBrokeredMessage(byte[] body, IDictionary<string, object> messageContext, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(null, body, messageContext, destination, bodyConverter) {}

        public OutboundBrokeredMessage(string messageId, object message, IDictionary<string, object> messageContext, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageId, bodyConverter.Convert(message), messageContext, destination, bodyConverter) {}

        public OutboundBrokeredMessage(string messageId, object message, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageId, bodyConverter.Convert(message), new Dictionary<string, object>(), destination, bodyConverter) {}

        public OutboundBrokeredMessage(object message, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(bodyConverter.Convert(message), new Dictionary<string, object>(), destination, bodyConverter) {}

        public string MessageId { get; }
        public string Destination { get; }
        public byte[] Body { get; }
        public IDictionary<string, object> MessageContext { get; }

        public static OutboundBrokeredMessage Forward(InboundBrokeredMessage messageToForward, string forwardDestination) 
            => new OutboundBrokeredMessage(Guid.NewGuid().ToString(), messageToForward.Body, (IDictionary<string, object>)messageToForward.MessageContext, forwardDestination, messageToForward.BodyConverter);

        public string Stringify() 
            => _bodyConverter.Stringify(Body);

        public OutboundBrokeredMessage WithTimeToLive(TimeSpan timeToLive)
        {
            MessageContext[MessageBrokers.MessageContext.TimeToLive] = timeToLive;
            return this;
        }

        public OutboundBrokeredMessage RefreshTimeToLive()
        {
            var expiryTimeUtc = (DateTime?)GetMessageContextByKey(MessageBrokers.MessageContext.ExpiryTimeUtc);
            if (expiryTimeUtc != null)
            {
                var ttl = expiryTimeUtc.Value - DateTime.UtcNow;
                if (ttl.Duration().TotalMilliseconds > 0)
                {
                    WithTimeToLive(ttl);
                }
                else
                {
                    WithTimeToLive(TimeSpan.Zero);
                }
            }
            return this;
        }

        public OutboundBrokeredMessage WithCorrelationId(string correlationId)
        {
            MessageContext[MessageBrokers.MessageContext.CorrelationId] = correlationId;
            return this;
        }

        public TransactionMode GetTransactionMode()
        {
            if (MessageContext.TryGetValue(MessageBrokers.MessageContext.TransactionMode, out var transactionMode))
            {
                return (TransactionMode)transactionMode;
            }
            else
            {
                return TransactionMode.FullAtomicityViaInfrastructure;
            }
        }

        public TimeSpan? GetTimeToLive()
        {
            var ttl = GetMessageContextByKey(MessageBrokers.MessageContext.TimeToLive);
            if (ttl == null)
            {
                return null;
            }

            if (ttl is TimeSpan ts)
            {
                return ts;
            }
            else
            {
                return TimeSpan.Parse((string)ttl);
            }
        }

        public string GetCorrelationId()
        {
            return (string)GetMessageContextByKey(MessageBrokers.MessageContext.CorrelationId);
        }

        public string GetReplyToAddress()
        {
            return (string)GetMessageContextByKey(MessageBrokers.MessageContext.ReplyToAddress);
        }

        public string GetReplyToGroupId()
        {
            return (string)GetMessageContextByKey(MessageBrokers.MessageContext.ReplyToGroupId);
        }

        public string GetGroupId()
        {
            return (string)GetMessageContextByKey(MessageBrokers.MessageContext.GroupId);
        }

        public string GetSubject()
        {
            return (string)GetMessageContextByKey(MessageBrokers.MessageContext.Subject);
        }

        public string GetContentType()
        {
            return _bodyConverter.ContentType;
        }
       
        internal OutboundBrokeredMessage ClearReplyToProperties()
        {
            MessageContext.Remove(MessageBrokers.MessageContext.ReplyToAddress);
            MessageContext.Remove(MessageBrokers.MessageContext.ReplyToGroupId);
            return this;
        }

        public object GetMessageContextByKey(string key)
        {
            if (MessageContext.TryGetValue(key, out var output))
            {
                return output;
            }
            else
            {
                return null;
            }
        }
    }
}
