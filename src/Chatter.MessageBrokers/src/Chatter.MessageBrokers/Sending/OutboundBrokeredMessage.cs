using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Sending
{
    public class OutboundBrokeredMessage
    {
        private readonly IBrokeredMessageBodyConverter _bodyConverter;

        [System.Text.Json.Serialization.JsonConstructor]
        internal OutboundBrokeredMessage(string messageId, byte[] body, IDictionary<string, object> messageContext, string destination)
        {
            MessageId = messageId;
            Body = body ?? throw new ArgumentNullException(nameof(body));
            Destination = destination;
            MessageContext = messageContext ?? new ConcurrentDictionary<string, object>();
        }

        public OutboundBrokeredMessage(string messageId, byte[] body, IDictionary<string, object> messageContext, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageId, body, messageContext, destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentException($"A destination is required for an {typeof(OutboundBrokeredMessage).Name}.", nameof(destination));
            }

            _bodyConverter = bodyConverter ?? throw new ArgumentNullException(nameof(bodyConverter));
            MessageContext[MessageBrokers.MessageContext.ContentType] = _bodyConverter.ContentType;

            if (string.IsNullOrWhiteSpace(CorrelationId))
            {
                WithCorrelationId(Guid.NewGuid().ToString());
            }
        }

        public OutboundBrokeredMessage(IMessageIdGenerator messageIdGenerator, byte[] body, IDictionary<string, object> messageContext, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageIdGenerator?.GenerateId(body).ToString(), body, messageContext, destination, bodyConverter) { }

        public OutboundBrokeredMessage(string messageId, object message, IDictionary<string, object> messageContext, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageId, bodyConverter.Convert(message), messageContext, destination, bodyConverter) { }

        public OutboundBrokeredMessage(IMessageIdGenerator messageIdGenerator, object message, IDictionary<string, object> messageContext, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageIdGenerator, bodyConverter.Convert(message), messageContext, destination, bodyConverter) { }

        public string MessageId { get; }
        public string Destination { get; }
        public byte[] Body { get; }
        public IDictionary<string, object> MessageContext { get; }

        public string Stringify()
            => _bodyConverter.Stringify(Body);

        public OutboundBrokeredMessage WithTimeToLive(TimeSpan timeToLive)
        {
            MessageContext[MessageBrokers.MessageContext.TimeToLive] = timeToLive;
            return this;
        }

        public string CorrelationId => GetMessageContextByKey<string>(MessageBrokers.MessageContext.CorrelationId);
        public string ContentType => _bodyConverter.ContentType;
        public string InfrastructureType => GetMessageContextByKey<string>(MessageBrokers.MessageContext.InfrastructureType);
        // A live-receive context holds a native int here, but an outbox-replayed context (deserialized
        // JSON) materializes the numeric value to a boxed long (see MessageContext.MaterializePersistedContextValue,
        // matching Newtonsoft). Unboxing a boxed long with (int) throws InvalidCastException, so read as
        // object and convert — mirroring GetTimeToLive()'s tolerant TimeSpan-or-string handling below.
        // Absent key yields null, preserving the prior default(int) == 0 behavior.
        public int ReceiveAttempts
        {
            get
            {
                var receiveAttempts = GetMessageContextByKey(MessageBrokers.MessageContext.ReceiveAttempts);
                return receiveAttempts == null ? default : Convert.ToInt32(receiveAttempts);
            }
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

        public TValue GetMessageContextByKey<TValue>(string key)
        {
            if (MessageContext.TryGetValue(key, out var output))
            {
                return (TValue)output;
            }
            else
            {
                return default;
            }
        }

        public object GetMessageContextByKey(string key) => GetMessageContextByKey<object>(key);
    }
}
