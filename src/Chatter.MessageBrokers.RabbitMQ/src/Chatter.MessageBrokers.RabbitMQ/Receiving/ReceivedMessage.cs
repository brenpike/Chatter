using System.Collections.Generic;
using RabbitMQ.Client;

namespace Chatter.MessageBrokers.RabbitMQ.Receiving
{
    /// <summary>
    /// A single AMQP delivery buffered by <see cref="RabbitMqReceiver"/> between the push consumer and the
    /// blocking pull of <c>ReceiveMessageAsync</c>. Carries the raw body, the broker-assigned delivery tag,
    /// the epoch of the receive channel that delivered it (used to detect a stale-channel ack), the delivery
    /// headers (including the native <c>x-delivery-count</c> for quorum queues and the adapter's
    /// <c>x-chatter-delivery-count</c> for classic queues), the source exchange / routing key, the
    /// broker's redelivered flag, and the curated set of delivered native AMQP properties that must be
    /// re-applied when the receiver republishes the message on a nack-redelivery or deadletter hop.
    /// </summary>
    public sealed class ReceivedMessage
    {
        public ReceivedMessage(byte[] body,
                               ulong deliveryTag,
                               long channelEpoch,
                               IReadOnlyDictionary<string, object> headers,
                               string exchange,
                               string routingKey,
                               bool redelivered,
                               string messageId,
                               string expiration = null,
                               byte? priority = null,
                               AmqpTimestamp? timestamp = null,
                               string type = null,
                               string appId = null,
                               string contentEncoding = null,
                               string contentType = null,
                               string correlationId = null)
        {
            Body = body;
            DeliveryTag = deliveryTag;
            ChannelEpoch = channelEpoch;
            Headers = headers ?? new Dictionary<string, object>();
            Exchange = exchange;
            RoutingKey = routingKey;
            Redelivered = redelivered;
            MessageId = messageId;
            Expiration = expiration;
            Priority = priority;
            Timestamp = timestamp;
            Type = type;
            AppId = appId;
            ContentEncoding = contentEncoding;
            ContentType = contentType;
            CorrelationId = correlationId;
        }

        /// <summary>The raw message body bytes as delivered by the broker.</summary>
        public byte[] Body { get; }

        /// <summary>The broker-assigned delivery tag, valid only on the channel identified by <see cref="ChannelEpoch"/>.</summary>
        public ulong DeliveryTag { get; }

        /// <summary>The epoch of the receive channel that delivered this message.</summary>
        public long ChannelEpoch { get; }

        /// <summary>The delivery headers, including any native or adapter delivery-count header.</summary>
        public IReadOnlyDictionary<string, object> Headers { get; }

        /// <summary>The exchange the message was published to.</summary>
        public string Exchange { get; }

        /// <summary>The routing key the message was published with.</summary>
        public string RoutingKey { get; }

        /// <summary>True if the broker flagged this delivery as a redelivery.</summary>
        public bool Redelivered { get; }

        /// <summary>The broker-assigned message id, or null when the publisher did not set one.</summary>
        public string MessageId { get; }

        /// <summary>
        /// The delivered native AMQP per-message TTL (<c>BasicProperties.Expiration</c>, milliseconds string), or
        /// null when the delivery carried none. Re-applied on a nack-redelivery republish so a classic-queue
        /// message published with a TimeToLive keeps its TTL across the republish; intentionally dropped on the
        /// deadletter hop so a dead-lettered message does not auto-expire.
        /// </summary>
        public string Expiration { get; }

        /// <summary>The delivered native AMQP <c>Priority</c>, or null when the delivery carried none. Re-applied on every republish hop.</summary>
        public byte? Priority { get; }

        /// <summary>The delivered native AMQP <c>Timestamp</c>, or null when the delivery carried none. Re-applied on every republish hop.</summary>
        public AmqpTimestamp? Timestamp { get; }

        /// <summary>The delivered native AMQP <c>Type</c>, or null when the delivery carried none. Re-applied on every republish hop.</summary>
        public string Type { get; }

        /// <summary>The delivered native AMQP <c>AppId</c>, or null when the delivery carried none. Re-applied on every republish hop.</summary>
        public string AppId { get; }

        /// <summary>The delivered native AMQP <c>ContentEncoding</c>, or null when the delivery carried none. Re-applied on every republish hop.</summary>
        public string ContentEncoding { get; }

        /// <summary>The delivered native AMQP <c>ContentType</c>, or null when the delivery carried none. Re-applied on every republish hop.</summary>
        public string ContentType { get; }

        /// <summary>The delivered native AMQP <c>CorrelationId</c>, or null when the delivery carried none. Re-applied on every republish hop.</summary>
        public string CorrelationId { get; }
    }
}
