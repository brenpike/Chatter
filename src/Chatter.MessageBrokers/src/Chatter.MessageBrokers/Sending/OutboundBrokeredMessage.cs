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
        public int ReceiveAttempts
        {
            get
            {
                // INVARIANT: ReceiveAttempts converts rather than kind-testing. A live-receive context holds a native int,
                // but an outbox-replayed context materializes the number to a boxed long
                // (MessageContext.MaterializePersistedContextValue), which TryGetMessageContextByKey<int> would read as
                // absent. Oracles: MustExposeReceiveAttemptsReadableAsInt (SqlServiceBroker tests) and
                // MustReadTypedContextValuesAfterOutboxSerializeMaterializeRoundTrip (AzureServiceBus tests) — reading
                // through TryGetMessageContextByKey<int> instead of Convert.ToInt32 reddens both.
                var receiveAttempts = GetMessageContextByKey(MessageBrokers.MessageContext.ReceiveAttempts);
                if (receiveAttempts == null)
                {
                    return default;
                }

                try
                {
                    return Convert.ToInt32(receiveAttempts);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
                {
                    // INVARIANT: a value Convert.ToInt32 cannot convert reads as absent (0) rather than faulting the read.
                    // Oracles: MustReadReceiveAttemptsAsZeroWhenPersistedKindIsNotConvertible (InvalidCastException),
                    // MustReadReceiveAttemptsAsZeroWhenPersistedStringIsNotNumeric (FormatException) and
                    // MustReadReceiveAttemptsAsZeroWhenPersistedNumberOverflowsAnInt (OverflowException) — dropping an
                    // exception type from this filter reddens its fact.
                    return default;
                }
            }
        }

        public OutboundBrokeredMessage RefreshTimeToLive()
        {
            if (TryGetMessageContextByKey<DateTime>(MessageBrokers.MessageContext.ExpiryTimeUtc, out var expiryTimeUtc))
            {
                var ttl = expiryTimeUtc - DateTime.UtcNow;
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
            if (TryGetMessageContextByKey<TimeSpan>(MessageBrokers.MessageContext.TimeToLive, out var timeToLive))
            {
                return timeToLive;
            }

            if (TryGetMessageContextByKey<string>(MessageBrokers.MessageContext.TimeToLive, out var timeToLiveText)
                && TimeSpan.TryParse(timeToLiveText, out var parsedTimeToLive))
            {
                return parsedTimeToLive;
            }

            return null;
        }

        public TValue GetMessageContextByKey<TValue>(string key)
            => TryGetMessageContextByKey<TValue>(key, out var value) ? value : default;

        /// <summary>
        /// Reads the message context value stored under <paramref name="key"/> when that value is a <typeparamref name="TValue"/>.
        /// </summary>
        /// <param name="key">The message context key to read.</param>
        /// <param name="value">The stored value when found; otherwise <see langword="default"/>.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="key"/> is present and its value is a <typeparamref name="TValue"/>;
        /// otherwise <see langword="false"/>. No conversion is attempted, so a stored <see cref="long"/> is not found as an <see cref="int"/>.
        /// </returns>
        public bool TryGetMessageContextByKey<TValue>(string key, out TValue value)
        {
            // INVARIANT: this is the only place a message context value becomes a TValue, and it kind-tests rather than
            // casts. An outbox-replayed context is deserialized JSON, so a value may come back as a different kind than it
            // was stamped as (a number becomes a boxed long); a hard cast here threw InvalidCastException on every replay
            // attempt. Oracles: MustReadAMismatchedKindAsAbsentFromTheTypedAccessor and
            // MustReportAMismatchedKindAsNotFoundFromTheTryAccessor — replacing `output is TValue typed` with a
            // `(TValue)output` cast reddens both.
            if (MessageContext.TryGetValue(key, out var output) && output is TValue typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public object GetMessageContextByKey(string key) => GetMessageContextByKey<object>(key);
    }
}
