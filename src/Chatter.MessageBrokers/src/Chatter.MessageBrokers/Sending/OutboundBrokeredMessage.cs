using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Saga;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Sending
{
    public class OutboundBrokeredMessage
    {
        private readonly IBrokeredMessageBodyConverter _bodyConverter;

        public OutboundBrokeredMessage(string messageId, byte[] body, IDictionary<string, object> applicationProperties, string destination, IBrokeredMessageBodyConverter bodyConverter)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentException($"A destination is required for an {typeof(OutboundBrokeredMessage).Name}.", nameof(destination));
            }

            ApplicationProperties = applicationProperties ?? new ConcurrentDictionary<string, object>();

            MessageId = messageId;
            Body = body ?? throw new ArgumentNullException(nameof(body));
            Destination = destination;
            _bodyConverter = bodyConverter ?? throw new ArgumentNullException(nameof(bodyConverter));
            ApplicationProperties[Headers.ContentType] = _bodyConverter.ContentType;

            if (string.IsNullOrWhiteSpace(GetCorrelationId()))
            {
                WithCorrelationId(Guid.NewGuid().ToString());
            }
        }

        public OutboundBrokeredMessage(byte[] body, IDictionary<string, object> applicationProperties, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(null, body, applicationProperties, destination, bodyConverter)
        {
        }

        public OutboundBrokeredMessage(string messageId, object message, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageId, bodyConverter.Convert(message), new Dictionary<string, object>(), destination, bodyConverter)
        {
        }

        public OutboundBrokeredMessage(object message, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(bodyConverter.Convert(message), new Dictionary<string, object>(), destination, bodyConverter)
        {
        }

        public string MessageId { get; }
        public string Destination { get; }
        public byte[] Body { get; }
        public IDictionary<string, object> ApplicationProperties { get; }

        public static OutboundBrokeredMessage Forward(InboundBrokeredMessage messageToForward, string forwardDestination)
        {
            var outbound = new OutboundBrokeredMessage(messageToForward.Body, (IDictionary<string, object>)messageToForward.ApplicationProperties, forwardDestination, messageToForward.BodyConverter);
            return outbound.RefreshTimeToLive();
        }

        public OutboundBrokeredMessage WithTransactionMode(TransactionMode transactionMode)
        {
            ApplicationProperties[Headers.TransactionMode] = (byte)transactionMode;
            return this;
        }

        public string Stringify() 
            => _bodyConverter.Stringify(Body);

        public TransactionMode GetTransactionMode()
        {
            if (ApplicationProperties.TryGetValue(Headers.TransactionMode, out var transactionMode))
            {
                return (TransactionMode)transactionMode;
            }
            else
            {
                return TransactionMode.FullAtomicityViaInfrastructure;
            }
        }

        public OutboundBrokeredMessage WithTimeToLive(TimeSpan timeToLive)
        {
            ApplicationProperties[Headers.TimeToLive] = timeToLive;
            return this;
        }

        public TimeSpan? GetTimeToLive()
        {
            return (TimeSpan?)GetApplicationPropertyByKey(Headers.TimeToLive);
        }

        public OutboundBrokeredMessage WithSagaStatus(SagaStatusEnum sagaStatus)
        {
            ApplicationProperties[Headers.SagaStatus] = (byte)sagaStatus;
            return this;
        }

        public OutboundBrokeredMessage WithSagaId(string sagaId)
        {
            ApplicationProperties[Headers.SagaId] = sagaId;
            return this;
        }

        public OutboundBrokeredMessage RefreshTimeToLive()
        {
            var expiryTimeUtc = (DateTime?)GetApplicationPropertyByKey(Headers.ExpiryTimeUtc);
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
            ApplicationProperties[Headers.CorrelationId] = correlationId;
            return this;
        }

        public string GetCorrelationId()
        {
            return (string)GetApplicationPropertyByKey(Headers.CorrelationId);
        }

        public OutboundBrokeredMessage WithReplyToAddress(string replyTo)
        {
            ApplicationProperties[Headers.ReplyTo] = replyTo;
            return this;
        }

        public string GetReplyToAddress()
        {
            return (string)GetApplicationPropertyByKey(Headers.ReplyTo);
        }

        public OutboundBrokeredMessage WithReplyToGroupId(string replyToGroupId)
        {
            ApplicationProperties[Headers.ReplyToGroupId] = replyToGroupId;
            return this;
        }

        public string GetReplyToGroupId()
        {
            return (string)GetApplicationPropertyByKey(Headers.ReplyToGroupId);
        }

        public OutboundBrokeredMessage WithGroupId(string groupId)
        {
            ApplicationProperties[Headers.GroupId] = groupId;
            return this;
        }

        public string GetGroupId()
        {
            return (string)GetApplicationPropertyByKey(Headers.GroupId);
        }

        public OutboundBrokeredMessage WithSubject(string subject)
        {
            ApplicationProperties[Headers.Subject] = subject;
            return this;
        }

        public string GetSubject()
        {
            return (string)GetApplicationPropertyByKey(Headers.Subject);
        }

        public string GetContentType()
        {
            return _bodyConverter.ContentType;
        }

        public OutboundBrokeredMessage WithTimeToLiveInMinutes(int minutes)
        {
            ApplicationProperties[Headers.TimeToLive] = TimeSpan.FromMinutes(minutes);
            return this;
        }

        public object GetApplicationPropertyByKey(string key)
        {
            if (ApplicationProperties.TryGetValue(key, out var output))
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
