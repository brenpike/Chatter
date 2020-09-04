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
            ApplicationProperties[MessageBrokers.ApplicationProperties.ContentType] = _bodyConverter.ContentType;

            if (string.IsNullOrWhiteSpace(GetCorrelationId()))
            {
                WithCorrelationId(Guid.NewGuid().ToString());
            }
        }

        public OutboundBrokeredMessage(byte[] body, IDictionary<string, object> applicationProperties, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(null, body, applicationProperties, destination, bodyConverter) {}

        public OutboundBrokeredMessage(string messageId, object message, IDictionary<string, object> applicationProperties, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageId, bodyConverter.Convert(message), applicationProperties, destination, bodyConverter) {}

        public OutboundBrokeredMessage(string messageId, object message, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(messageId, bodyConverter.Convert(message), new Dictionary<string, object>(), destination, bodyConverter) {}

        public OutboundBrokeredMessage(object message, string destination, IBrokeredMessageBodyConverter bodyConverter)
            : this(bodyConverter.Convert(message), new Dictionary<string, object>(), destination, bodyConverter) {}

        public string MessageId { get; }
        public string Destination { get; }
        public byte[] Body { get; }
        public IDictionary<string, object> ApplicationProperties { get; }

        public static OutboundBrokeredMessage Forward(InboundBrokeredMessage messageToForward, string forwardDestination) 
            => new OutboundBrokeredMessage(Guid.NewGuid().ToString(), messageToForward.Body, (IDictionary<string, object>)messageToForward.ApplicationProperties, forwardDestination, messageToForward.BodyConverter);

        public string Stringify() 
            => _bodyConverter.Stringify(Body);

        public OutboundBrokeredMessage WithTimeToLive(TimeSpan timeToLive)
        {
            ApplicationProperties[MessageBrokers.ApplicationProperties.TimeToLive] = timeToLive;
            return this;
        }

        public OutboundBrokeredMessage RefreshTimeToLive()
        {
            var expiryTimeUtc = (DateTime?)GetApplicationPropertyByKey(MessageBrokers.ApplicationProperties.ExpiryTimeUtc);
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
            ApplicationProperties[MessageBrokers.ApplicationProperties.CorrelationId] = correlationId;
            return this;
        }

        public TransactionMode GetTransactionMode()
        {
            if (ApplicationProperties.TryGetValue(MessageBrokers.ApplicationProperties.TransactionMode, out var transactionMode))
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
            return (TimeSpan?)GetApplicationPropertyByKey(MessageBrokers.ApplicationProperties.TimeToLive);
        }

        public string GetCorrelationId()
        {
            return (string)GetApplicationPropertyByKey(MessageBrokers.ApplicationProperties.CorrelationId);
        }

        public string GetReplyToAddress()
        {
            return (string)GetApplicationPropertyByKey(MessageBrokers.ApplicationProperties.ReplyToAddress);
        }

        public string GetReplyToGroupId()
        {
            return (string)GetApplicationPropertyByKey(MessageBrokers.ApplicationProperties.ReplyToGroupId);
        }

        public string GetGroupId()
        {
            return (string)GetApplicationPropertyByKey(MessageBrokers.ApplicationProperties.GroupId);
        }

        public string GetSubject()
        {
            return (string)GetApplicationPropertyByKey(MessageBrokers.ApplicationProperties.Subject);
        }

        public string GetContentType()
        {
            return _bodyConverter.ContentType;
        }

        internal OutboundBrokeredMessage WithFailureDetails(string failureDetails)
        {
            ApplicationProperties[MessageBrokers.ApplicationProperties.FailureDetails] = failureDetails;
            return this;
        }

        internal OutboundBrokeredMessage WithFailureDescription(string failureDescription)
        {
            ApplicationProperties[MessageBrokers.ApplicationProperties.FailureDescription] = failureDescription;
            return this;
        }

        internal OutboundBrokeredMessage SetFailure()
        {
            ApplicationProperties[MessageBrokers.ApplicationProperties.IsError] = true;
            ApplicationProperties[MessageBrokers.ApplicationProperties.SagaStatus] = (byte)SagaStatusEnum.Failed;
            return this;
        }

        internal OutboundBrokeredMessage ClearReplyToProperties()
        {
            ApplicationProperties.Remove(MessageBrokers.ApplicationProperties.ReplyToAddress);
            ApplicationProperties.Remove(MessageBrokers.ApplicationProperties.ReplyToGroupId);
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
