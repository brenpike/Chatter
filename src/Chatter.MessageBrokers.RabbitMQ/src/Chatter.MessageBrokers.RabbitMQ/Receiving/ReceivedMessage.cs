using System.Collections.Generic;

namespace Chatter.MessageBrokers.RabbitMQ.Receiving
{
    /// <summary>
    /// A single AMQP delivery buffered by <see cref="RabbitMqReceiver"/> between the push consumer and the
    /// blocking pull of <c>ReceiveMessageAsync</c>. Carries the raw body, the broker-assigned delivery tag,
    /// the epoch of the receive channel that delivered it (used to detect a stale-channel ack), the delivery
    /// headers (including the native <c>x-delivery-count</c> for quorum queues and the adapter's
    /// <c>x-chatter-delivery-count</c> for classic queues), the source exchange / routing key, and the
    /// broker's redelivered flag.
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
                               string messageId)
        {
            Body = body;
            DeliveryTag = deliveryTag;
            ChannelEpoch = channelEpoch;
            Headers = headers ?? new Dictionary<string, object>();
            Exchange = exchange;
            RoutingKey = routingKey;
            Redelivered = redelivered;
            MessageId = messageId;
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
    }
}
