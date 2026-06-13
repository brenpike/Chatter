using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving
{
    // A recording IChannel the in-memory connection source hands to RunOnReceiveChannelAsync and to publish
    // rentals. It implements only the members RabbitMqReceiver/RabbitMqSender exercise — ack, nack, publish,
    // consume-registration, QoS — and records each so tests assert off the recordings. Every other IChannel
    // member throws NotImplementedException: reaching one is a signal the production code took an untested
    // path, which a test should surface rather than silently accept. Reports IsOpen == false so a rental's
    // ReturnPublishChannel disposes (not re-pools) it, leaving its recordings intact for assertions.
    internal sealed class RecordingChannel : IChannel
    {
        public List<AckRecord> Acks { get; } = new List<AckRecord>();
        public List<NackRecord> Nacks { get; } = new List<NackRecord>();
        public List<PublishRecord> Publishes { get; } = new List<PublishRecord>();
        public IAsyncBasicConsumer RegisteredConsumer { get; private set; }
        public ushort? LastQosPrefetchCount { get; private set; }

        public ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default)
        {
            Acks.Add(new AckRecord(deliveryTag, multiple));
            return default;
        }

        public ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default)
        {
            Nacks.Add(new NackRecord(deliveryTag, multiple, requeue));
            return default;
        }

        // Opt-in publish-fault seam: when non-null, BasicPublishAsync records the publish (so recording
        // assertions hold), then faults the returned task with this exception — modeling the broker's
        // 7.2.1 confirm-tracking fault on an unroutable mandatory basic.return. Default null = success.
        public Exception PublishFault { get; set; }

        public ValueTask BasicPublishAsync<TProperties>(string exchange, string routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
            where TProperties : IReadOnlyBasicProperties, IAmqpHeader
        {
            Publishes.Add(new PublishRecord(
                exchange,
                routingKey,
                mandatory,
                basicProperties.ContentType,
                basicProperties.MessageId,
                basicProperties.Expiration,
                basicProperties.IsPriorityPresent() ? basicProperties.Priority : (byte?)null,
                basicProperties.IsTimestampPresent() ? basicProperties.Timestamp : (AmqpTimestamp?)null,
                basicProperties.IsTypePresent() ? basicProperties.Type : null,
                basicProperties.IsAppIdPresent() ? basicProperties.AppId : null,
                basicProperties.IsContentEncodingPresent() ? basicProperties.ContentEncoding : null,
                basicProperties.IsCorrelationIdPresent() ? basicProperties.CorrelationId : null,
                basicProperties.Headers is null ? null : new Dictionary<string, object>(basicProperties.Headers),
                body.ToArray()));

            if (PublishFault is not null)
            {
                return ValueTask.FromException(PublishFault);
            }

            return default;
        }

        public ValueTask BasicPublishAsync<TProperties>(CachedString exchange, CachedString routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
            where TProperties : IReadOnlyBasicProperties, IAmqpHeader
            => throw new NotImplementedException();

        public Task<string> BasicConsumeAsync(string queue, bool autoAck, string consumerTag, bool noLocal, bool exclusive, IDictionary<string, object> arguments, IAsyncBasicConsumer consumer, CancellationToken cancellationToken = default)
        {
            RegisteredConsumer = consumer;
            return Task.FromResult("in-memory-consumer-tag");
        }

        public Task BasicQosAsync(uint prefetchSize, ushort prefetchCount, bool global, CancellationToken cancellationToken = default)
        {
            LastQosPrefetchCount = prefetchCount;
            return Task.CompletedTask;
        }

        // --- Unused IChannel surface: a production path reaching any of these is untested by design ---------

        public int ChannelNumber => throw new NotImplementedException();
        public ShutdownEventArgs CloseReason => throw new NotImplementedException();
        public IAsyncBasicConsumer DefaultConsumer { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool IsClosed => true;
        public bool IsOpen => false;
        public string CurrentQueue => throw new NotImplementedException();
        public TimeSpan ContinuationTimeout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync { add { } remove { } }
        public event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync { add { } remove { } }
        public event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync { add { } remove { } }
        public event AsyncEventHandler<CallbackExceptionEventArgs> CallbackExceptionAsync { add { } remove { } }
        public event AsyncEventHandler<FlowControlEventArgs> FlowControlAsync { add { } remove { } }
        public event AsyncEventHandler<ShutdownEventArgs> ChannelShutdownAsync { add { } remove { } }

        public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task BasicCancelAsync(string consumerTag, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BasicGetResult> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task CloseAsync(ushort replyCode, string replyText, bool abort, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task CloseAsync(ShutdownEventArgs reason, bool abort) => throw new NotImplementedException();
        public Task CloseAsync(ShutdownEventArgs reason, bool abort, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task ExchangeDeclareAsync(string exchange, string type, bool durable, bool autoDelete, IDictionary<string, object> arguments = null, bool passive = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ExchangeDeclarePassiveAsync(string exchange, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ExchangeDeleteAsync(string exchange, bool ifUnused = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ExchangeBindAsync(string destination, string source, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ExchangeUnbindAsync(string destination, string source, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<QueueDeclareOk> QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object> arguments = null, bool passive = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<QueueDeclareOk> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<uint> QueueDeleteAsync(string queue, bool ifUnused, bool ifEmpty, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<uint> QueuePurgeAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task QueueBindAsync(string queue, string exchange, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task QueueUnbindAsync(string queue, string exchange, string routingKey, IDictionary<string, object> arguments = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<uint> MessageCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<uint> ConsumerCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task TxCommitAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task TxRollbackAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task TxSelectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

        // Records whether the channel was disposed, so a test can assert an orphaned rental disposed its channel.
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }

    internal readonly struct AckRecord
    {
        public AckRecord(ulong deliveryTag, bool multiple)
        {
            DeliveryTag = deliveryTag;
            Multiple = multiple;
        }

        public ulong DeliveryTag { get; }
        public bool Multiple { get; }
    }

    internal readonly struct NackRecord
    {
        public NackRecord(ulong deliveryTag, bool multiple, bool requeue)
        {
            DeliveryTag = deliveryTag;
            Multiple = multiple;
            Requeue = requeue;
        }

        public ulong DeliveryTag { get; }
        public bool Multiple { get; }
        public bool Requeue { get; }
    }

    internal sealed class PublishRecord
    {
        public PublishRecord(string exchange,
                             string routingKey,
                             bool mandatory,
                             string contentType,
                             string messageId,
                             string expiration,
                             byte? priority,
                             AmqpTimestamp? timestamp,
                             string type,
                             string appId,
                             string contentEncoding,
                             string correlationId,
                             IDictionary<string, object> headers,
                             byte[] body)
        {
            Exchange = exchange;
            RoutingKey = routingKey;
            Mandatory = mandatory;
            ContentType = contentType;
            MessageId = messageId;
            Expiration = expiration;
            Priority = priority;
            Timestamp = timestamp;
            Type = type;
            AppId = appId;
            ContentEncoding = contentEncoding;
            CorrelationId = correlationId;
            Headers = headers;
            Body = body;
        }

        public string Exchange { get; }
        public string RoutingKey { get; }
        public bool Mandatory { get; }
        public string ContentType { get; }
        public string MessageId { get; }
        // The native BasicProperties.Expiration (ms string) sourced from the publish, so a test can assert that
        // a TimeSpan TimeToLive was lifted onto the native property rather than the field table — and that a
        // republish hop re-applied (or, on deadletter, dropped) the carried native Expiration.
        public string Expiration { get; }
        // The remaining carried native AMQP properties sourced from the publish (null when the published
        // BasicProperties did not set them), so a republish test can assert each travels (or is dropped) per hop.
        public byte? Priority { get; }
        public AmqpTimestamp? Timestamp { get; }
        public string Type { get; }
        public string AppId { get; }
        public string ContentEncoding { get; }
        public string CorrelationId { get; }
        public IDictionary<string, object> Headers { get; }
        public byte[] Body { get; }
    }
}
