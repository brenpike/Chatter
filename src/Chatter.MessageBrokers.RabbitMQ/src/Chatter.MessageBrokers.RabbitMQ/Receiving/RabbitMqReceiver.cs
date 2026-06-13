using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.RabbitMQ.Receiving
{
    /// <summary>
    /// The RabbitMQ <see cref="IMessagingInfrastructureReceiver"/>. Registers an AMQP push consumer on the
    /// single serialized receive channel (via <see cref="IRabbitMqConnectionSource"/>), buffers each delivery
    /// into a bounded <see cref="Channel{T}"/>, and surfaces them one-at-a-time through the blocking pull of
    /// <see cref="ReceiveMessageAsync"/> so the core receive loop async-parks when no message is available.
    /// </summary>
    /// <remarks>
    /// INVARIANT: every receive-channel operation (consume registration, ack, nack, deadletter ack) runs only
    /// under the connection-source gate — settlement via <see cref="IRabbitMqConnectionSource.RunOnReceiveChannelAsync{TResult}"/>,
    /// consume registration via <see cref="IRabbitMqConnectionSource.StartReceivingAsync"/> — because AMQP
    /// channels are not thread-safe and a delivery tag is valid only on its owning channel.
    /// INVARIANT (closed-by-construction epoch): the receiver hands the source a consume-registration delegate;
    /// the source re-runs it on every receive-channel (re)creation (cold start, lazy recreate, recovery) with the
    /// freshly-bumped epoch, which the delegate stamps onto every delivery it buffers. The delegate does NOT close
    /// over a one-time epoch — each (re)registration receives the current epoch as a parameter — so a
    /// post-recovery delivery always carries the post-recovery epoch and a pre-recovery in-flight delivery keeps
    /// the pre-recovery epoch. The bounded <c>_buffer</c> is created ONCE in InitializeAsync and is NOT recreated
    /// on re-registration, so deliveries buffered before recovery survive the consumer swap.
    /// INVARIANT: ack/nack/deadletter are epoch-guarded — if the delivery's carried channel-epoch no longer
    /// matches the current receive-channel epoch the channel was recycled, the broker has already redelivered
    /// the message, and the settlement is a no-op (never a false-ack against a recycled delivery tag).
    /// INVARIANT: <see cref="MessageContext.ReceiveAttempts"/> is stamped (as <see cref="int"/>) on every
    /// received message — the core's default <c>MessageDeliveryCountAsync</c> casts it unguarded.
    /// </remarks>
    public sealed class RabbitMqReceiver : IMessagingInfrastructureReceiver
    {
        // The native quorum-queue redelivery counter the broker increments per redelivery.
        private const string _nativeDeliveryCountHeader = "x-delivery-count";

        private readonly IRabbitMqConnectionSource _connectionSource;
        private readonly RabbitMqOptions _rabbitOptions;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly ILogger<RabbitMqReceiver> _logger;

        private Channel<ReceivedMessage> _buffer;
        private ReceiverOptions _options;
        private string _consumerTag;
        private int _prefetch;

        public RabbitMqReceiver(IRabbitMqConnectionSource connectionSource,
                                RabbitMqOptions rabbitOptions,
                                IBodyConverterFactory bodyConverterFactory,
                                ILogger<RabbitMqReceiver> logger)
        {
            _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
            _rabbitOptions = rabbitOptions ?? throw new ArgumentNullException(nameof(rabbitOptions));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            // Prefetch must keep enough unacknowledged deliveries in flight to saturate the core's workers, so
            // floor it at MaxConcurrentCalls. The bounded buffer mirrors that prefetch so the consumer never
            // accepts more than the broker is willing to leave unacknowledged.
            _prefetch = Math.Max(Math.Max(1, _rabbitOptions.Prefetch), options.MaxConcurrentCalls);
            _buffer = System.Threading.Channels.Channel.CreateBounded<ReceivedMessage>(
                new BoundedChannelOptions(_prefetch)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });

            // Hand the source the consume-registration delegate. The source runs it now and re-runs it on every
            // receive-channel (re)creation (lazy recreate, recovery) under the gate, passing the FRESH epoch each
            // time. The delegate stamps that per-invocation epoch onto every buffered delivery, so a later ack can
            // detect a recycled channel. The epoch is NOT closed over once at InitializeAsync — it is the per-call
            // parameter the source supplies, so a post-recovery re-registration stamps the post-recovery epoch.
            await _connectionSource.StartReceivingAsync(RegisterConsumerAsync, cancellationToken).ConfigureAwait(false);
        }

        // INVARIANT: invoked by the connection source under the receive gate on every (re)creation of the receive
        // channel, with <paramref name="epoch"/> being the freshly-bumped epoch of that channel. Registers a fresh
        // push consumer that stamps THIS epoch onto every delivery it buffers. The bounded buffer is NOT recreated
        // here (it is created once in InitializeAsync) so in-flight pre-recovery deliveries survive the swap.
        private async Task RegisterConsumerAsync(IChannel channel, long epoch, CancellationToken cancellationToken)
        {
            await channel.BasicQosAsync(prefetchSize: 0,
                                        prefetchCount: (ushort)_prefetch,
                                        global: false,
                                        cancellationToken: cancellationToken).ConfigureAwait(false);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (sender, delivery) => BufferDeliveryAsync(delivery, epoch);

            // Capture the latest consumer tag — it may change across recovery as the consumer is re-registered.
            _consumerTag = await channel.BasicConsumeAsync(queue: _options.MessageReceiverPath,
                                                           autoAck: false,
                                                           consumerTag: string.Empty,
                                                           noLocal: false,
                                                           exclusive: false,
                                                           arguments: null,
                                                           consumer: consumer,
                                                           cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // INVARIANT: the push consumer's only job is to wrap the delivery (carrying the registration-time epoch)
        // and enqueue it into the bounded buffer. When the buffer is full WriteAsync async-parks here, which
        // back-pressures the broker exactly to prefetch.
        // INVARIANT: the curated native AMQP property set is captured here ONCE through the single translation
        // contract (RabbitMqMessageTranslator.CaptureFacts) from the delivery's IReadOnlyBasicProperties, so every
        // native property the broker delivered survives onto ReceivedMessage as the republish-carry facts and is
        // re-applied through the translator on a republish hop. Without this capture the republish rebuilt
        // BasicProperties from scratch and dropped every delivered native property (e.g. a classic-queue per-message
        // TTL lost on the first redelivery).
        private async Task BufferDeliveryAsync(BasicDeliverEventArgs delivery, long epoch)
        {
            var properties = delivery.BasicProperties;
            var facts = RabbitMqMessageTranslator.CaptureFacts(properties);

            var received = new ReceivedMessage(body: delivery.Body.ToArray(),
                                               deliveryTag: delivery.DeliveryTag,
                                               channelEpoch: epoch,
                                               headers: properties?.Headers is { } headers
                                                   ? new Dictionary<string, object>(headers)
                                                   : new Dictionary<string, object>(),
                                               exchange: delivery.Exchange,
                                               routingKey: delivery.RoutingKey,
                                               redelivered: delivery.Redelivered,
                                               messageId: facts.MessageId,
                                               expiration: facts.Expiration,
                                               priority: facts.Priority,
                                               timestamp: facts.Timestamp,
                                               type: facts.Type,
                                               appId: facts.AppId,
                                               contentEncoding: facts.ContentEncoding,
                                               contentType: facts.ContentType,
                                               correlationId: facts.CorrelationId);

            await _buffer.Writer.WriteAsync(received, delivery.CancellationToken).ConfigureAwait(false);
        }

        public async Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            // Blocking pull: async-park when the buffer is empty (no poll, no spin) until the push consumer
            // enqueues a delivery or the loop token cancels.
            var received = await _buffer.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            var receiveAttempts = ResolveReceiveAttempts(received);

            // Seed the context through the SINGLE translation contract: the delivered header table is decoded
            // (string-typed header keys byte[]->string so the core's unguarded (string) cast holds), the native
            // frame fields with a core concept are surfaced (ContentType -> GAP B, CorrelationId dual-home), and
            // the native Expiration is reconstituted into MessageContext.TimeToLive (GAP A). The C-family natives
            // stay on the facts only (DECISION-B), never in the core context. This matches the other adapters that
            // copy inbound application properties before adding infrastructure stamps. Infrastructure keys are then
            // stamped ON TOP, so a fresh DeliveryTag / ChannelEpoch / InfrastructureType / ReceiveAttempts always
            // wins over any inbound value of the same key.
            var (headers, deliveredContentType) = RabbitMqMessageTranslator.ToCore(FactsFrom(received), received.Headers);

            // INVARIANT: the OUTBOUND dispatch-override command keys (TargetExchange / RoutingKey) must NEVER be
            // carried from an inbound delivery. Only .WithRabbitMqRouting writes them and the sender reads them; the
            // core seeds an outbound send's options from the inbound context, so preserving an inbound copy of these
            // would silently re-route every receive-then-send follow-up back toward the inbound queue. They are
            // stripped here (a round-tripped message can carry them because the sender publishes the full context as
            // headers), independent of the broker-supplied delivery.Exchange / delivery.RoutingKey, which are also
            // never stamped.
            headers.Remove(RabbitMqMessageContext.TargetExchange);
            headers.Remove(RabbitMqMessageContext.RoutingKey);

            headers[RabbitMqMessageContext.DeliveryTag] = received.DeliveryTag;
            headers[RabbitMqMessageContext.ChannelEpoch] = received.ChannelEpoch;
            headers[MessageContext.InfrastructureType] = RabbitMqMessageContext.InfrastructureType;
            // MANDATORY: stamped on EVERY message as an int. The core's default MessageDeliveryCountAsync
            // casts this value to (int) without a guard, so an absent or non-int value would throw there.
            headers[MessageContext.ReceiveAttempts] = receiveAttempts;

            var messageId = string.IsNullOrEmpty(received.MessageId) ? Guid.NewGuid().ToString() : received.MessageId;
            // GAP B: pick the body converter from the DELIVERED content-type when the delivery carried one, so the
            // emitted context deserializes with the converter that matches what the publisher actually serialized.
            // Fall back to the converter resolved for the configured MessageBodyType otherwise; an unknown
            // content-type falls back to the core JsonBodyConverter inside the factory (never throws).
            var bodyConverterContentType = string.IsNullOrWhiteSpace(deliveredContentType)
                ? _rabbitOptions.MessageBodyType
                : deliveredContentType;
            var bodyConverter = _bodyConverterFactory.CreateBodyConverter(bodyConverterContentType);
            var messageContext = new MessageBrokerContext(messageId,
                                                          received.Body,
                                                          headers,
                                                          _options.MessageReceiverPath,
                                                          cancellationToken,
                                                          bodyConverter);
            // Carry the raw delivery so the settlement methods recover the delivery tag and epoch.
            messageContext.Container.Include(received);

            return messageContext;
        }

        // Quorum queues expose the broker's native x-delivery-count (number of prior redeliveries); attempts is
        // that count + 1. Classic queues carry the adapter's own x-chatter-delivery-count holding the number of PRIOR
        // deliveries (0 when absent on the first delivery, advanced on each republish); attempts is likewise that
        // count + 1. BOTH paths floor the first delivery at attempt 1 — the shared receiver contract treats an actual
        // delivery as at least the first attempt, and the core deadletters only when deliveryCount >= MaxReceiveAttempts,
        // so without the +1 a classic poison message with maxReceiveAttempts: 1 would be handled twice before
        // deadlettering, unlike quorum.
        // The delivery-count headers are broker-supplied and untrusted: a negative or oversized value would, if
        // cast straight to int, stamp a negative or wrapped ReceiveAttempts that the core compares to
        // MaxReceiveAttempts — letting a poison message dodge deadlettering or get a bogus retry budget. Saturate
        // the raw long into a non-negative int before stamping.
        private int ResolveReceiveAttempts(ReceivedMessage received)
        {
            var headerKey = _rabbitOptions.QueueType == QueueType.Quorum
                ? _nativeDeliveryCountHeader
                : RabbitMqMessageContext.DeliveryCountHeader;

            var priorDeliveries = SaturateToNonNegativeInt(ReadHeaderAsLong(received.Headers, headerKey, 0L));
            return priorDeliveries == int.MaxValue ? int.MaxValue : priorDeliveries + 1;
        }

        // Clamp an untrusted broker-supplied counter into [0, int.MaxValue]: negatives become 0 and values above
        // int.MaxValue saturate, so a malformed header can never produce a negative or wrapped attempt count.
        private static int SaturateToNonNegativeInt(long value)
        {
            if (value <= 0L)
            {
                return 0;
            }

            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        public Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (!TryGetReceivedMessage(context, out var received))
            {
                _logger.LogTrace("No {receivedMessage} contained in context; nothing to acknowledge.", nameof(ReceivedMessage));
                return Task.FromResult(false);
            }

            return SettleOnReceiveChannelAsync(received, (channel) =>
                channel.BasicAckAsync(received.DeliveryTag, multiple: false, cancellationToken), cancellationToken);
        }

        public Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (!TryGetReceivedMessage(context, out var received))
            {
                _logger.LogTrace("No {receivedMessage} contained in context; nothing to negatively acknowledge.", nameof(ReceivedMessage));
                return Task.FromResult(false);
            }

            if (_rabbitOptions.QueueType == QueueType.Classic)
            {
                // Classic queues have no native redelivery counter: republish to the receiver's own queue with an
                // incremented x-chatter-delivery-count (publisher-confirmed) BEFORE acking the original, so the
                // attempt count survives reconnect and horizontal scaling and the message is never lost. The
                // delivered native Expiration is PRESERVED on this redelivery hop so a classic-queue message
                // published with a per-message TTL keeps its TTL across the republish.
                return RepublishThenAckAsync(received,
                                             destination: _options.MessageReceiverPath,
                                             headerOverrides: BuildClassicRedeliveryHeaders(received),
                                             preserveExpiration: true,
                                             cancellationToken: cancellationToken);
            }

            // Quorum queues: a plain requeue lets the broker increment the native x-delivery-count and redeliver.
            return SettleOnReceiveChannelAsync(received, (channel) =>
                channel.BasicNackAsync(received.DeliveryTag, multiple: false, requeue: true, cancellationToken), cancellationToken);
        }

        public Task<bool> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            if (!TryGetReceivedMessage(context, out var received))
            {
                _logger.LogTrace("No {receivedMessage} contained in context; nothing to deadletter.", nameof(ReceivedMessage));
                return Task.FromResult(false);
            }

            // Explicit republish (SSB-style) to the attribute-declared deadletter / error path, authoritative over
            // any broker-side DLX. The republish is publisher-confirmed BEFORE the original is acked so a crash
            // between the two yields at-most a duplicate, never loss.
            var destination = string.IsNullOrWhiteSpace(_options.DeadLetterQueuePath)
                ? _options.ErrorQueuePath
                : _options.DeadLetterQueuePath;

            // FAIL FAST when neither a deadletter nor an error path is configured: the republish below uses
            // `destination` as the default-exchange routing key with `mandatory: true`, so a null/blank destination
            // would be unroutable and surface a PublishException AFTER the message has already exhausted its retry
            // budget — leaving the original delivery un-acked and redelivered indefinitely (a poison-message hot
            // loop). Surfacing the misconfiguration here, with the queue that produced it, is actionable; an opaque
            // unroutable-publish fault on the Nth redelivery is not.
            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new InvalidOperationException(
                    $"Cannot deadletter a message from queue '{_options.MessageReceiverPath}': neither a dead-letter queue " +
                    $"({nameof(ReceiverOptions.DeadLetterQueuePath)}) nor an error queue ({nameof(ReceiverOptions.ErrorQueuePath)}) " +
                    "is configured for the receiver. Configure one so poison messages have a valid destination instead of being redelivered indefinitely.");
            }

            var headerOverrides = new Dictionary<string, object>
            {
                [MessageContext.FailureDetails] = deadLetterReason,
                [MessageContext.FailureDescription] = deadLetterErrorDescription
            };

            // The delivered native Expiration is DROPPED on the deadletter hop: a dead-letter queue is for
            // inspection, so a dead-lettered message must NOT auto-expire via the original per-message TTL. All
            // other carried native props travel (useful for DLQ inspection).
            return RepublishThenAckAsync(received, destination, headerOverrides, preserveExpiration: false, cancellationToken);
        }

        // INVARIANT: the republish (confirms-enabled publish channel) MUST complete before the original delivery
        // is acked, so a fault between them leaves the original un-acked and the broker redelivers it. The ack is
        // epoch-guarded; if the receive channel was recycled the ack is a no-op and the broker redelivers — the
        // republished copy is then the duplicate absorbed downstream.
        private async Task<bool> RepublishThenAckAsync(ReceivedMessage received,
                                                       string destination,
                                                       IReadOnlyDictionary<string, object> headerOverrides,
                                                       bool preserveExpiration,
                                                       CancellationToken cancellationToken)
        {
            await using var rental = await _connectionSource.AcquirePublishChannelAsync(cancellationToken).ConfigureAwait(false);

            // REPUBLISH boundary: route through the SINGLE translation contract so no hop rebuilds BasicProperties
            // independently. The carried facts (every delivered native, including the C-family — DECISION-B) and the
            // merged header overrides are re-emitted exactly like a fresh publish; preserveExpiration is the only
            // per-hop difference (true on nack-redelivery, false on deadletter).
            var properties = RabbitMqMessageTranslator.ToRepublishAmqp(
                FactsFrom(received),
                received.Headers,
                new RabbitMqMessageTranslator.RepublishOptions(preserveExpiration, headerOverrides));

            // Default-exchange convention: routing key == destination queue name.
            await rental.Channel.BasicPublishAsync(exchange: string.Empty,
                                                   routingKey: destination,
                                                   mandatory: true,
                                                   basicProperties: properties,
                                                   body: new ReadOnlyMemory<byte>(received.Body),
                                                   cancellationToken: cancellationToken).ConfigureAwait(false);

            return await SettleOnReceiveChannelAsync(received, (channel) =>
                channel.BasicAckAsync(received.DeliveryTag, multiple: false, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        // Projects the curated native AMQP properties carried on a buffered ReceivedMessage back into the
        // translator's NativeFacts carrier, so the receive translation (ToCore) and the republish translation
        // (ToRepublishAmqp) consume the SAME native-property shape the single capture point (CaptureFacts) produced.
        private static RabbitMqMessageTranslator.NativeFacts FactsFrom(ReceivedMessage received)
            => new RabbitMqMessageTranslator.NativeFacts(
                messageId: received.MessageId,
                expiration: received.Expiration,
                priority: received.Priority,
                timestamp: received.Timestamp,
                type: received.Type,
                appId: received.AppId,
                contentEncoding: received.ContentEncoding,
                contentType: received.ContentType,
                correlationId: received.CorrelationId);

        // INVARIANT: runs the settlement under the receive-channel gate and compares the delivery's carried epoch
        // to the current channel epoch. On a mismatch the channel was recycled since delivery — the delivery tag
        // is meaningless on the new channel and the broker has already redelivered — so the settlement is a no-op.
        private Task<bool> SettleOnReceiveChannelAsync(ReceivedMessage received,
                                                       Func<IChannel, ValueTask> settle,
                                                       CancellationToken cancellationToken)
        {
            return _connectionSource.RunOnReceiveChannelAsync(async (channel, currentEpoch) =>
            {
                if (received.ChannelEpoch != currentEpoch)
                {
                    _logger.LogTrace("Skipping settlement of delivery tag {deliveryTag}: carried epoch {carriedEpoch} no longer matches current channel epoch {currentEpoch}; broker will redeliver.",
                        received.DeliveryTag, received.ChannelEpoch, currentEpoch);
                    return false;
                }

                await settle(channel).ConfigureAwait(false);
                return true;
            }, cancellationToken);
        }

        // Normalize the untrusted incoming x-chatter-delivery-count the same way ResolveReceiveAttempts does
        // before incrementing it back onto the republished copy, so a hostile negative/oversized header cannot
        // poison the counter we stamp (which would otherwise let the message dodge or distort deadlettering).
        private IReadOnlyDictionary<string, object> BuildClassicRedeliveryHeaders(ReceivedMessage received)
        {
            var current = SaturateToNonNegativeInt(ReadHeaderAsLong(received.Headers, RabbitMqMessageContext.DeliveryCountHeader, 0L));
            var next = current == int.MaxValue ? int.MaxValue : current + 1;
            return new Dictionary<string, object>
            {
                [RabbitMqMessageContext.DeliveryCountHeader] = (long)next
            };
        }

        // AMQP integer headers arrive boxed as int/long/short/byte; a string-typed header arrives as a byte[].
        // Read the delivery-count header tolerantly so both the native x-delivery-count and the adapter's
        // x-chatter-delivery-count resolve to a long regardless of which boxed numeric type the client produced.
        private static long ReadHeaderAsLong(IReadOnlyDictionary<string, object> headers, string key, long defaultValue)
        {
            if (headers == null || !headers.TryGetValue(key, out var raw) || raw == null)
            {
                return defaultValue;
            }

            switch (raw)
            {
                case long asLong:
                    return asLong;
                case int asInt:
                    return asInt;
                case short asShort:
                    return asShort;
                case byte asByte:
                    return asByte;
                case uint asUInt:
                    return asUInt;
                case ushort asUShort:
                    return asUShort;
                case byte[] asBytes when long.TryParse(System.Text.Encoding.UTF8.GetString(asBytes), out var parsed):
                    return parsed;
                case string asString when long.TryParse(asString, out var parsed):
                    return parsed;
                default:
                    return defaultValue;
            }
        }

        private static bool TryGetReceivedMessage(MessageBrokerContext context, out ReceivedMessage received)
        {
            received = null;
            return context?.Container.TryGet(out received) ?? false;
        }

        public Task StopReceiver()
        {
            _buffer?.Writer.TryComplete();
            return Task.CompletedTask;
        }

        // CreateLocalTransaction returns null to match the core default; full-atomicity transaction handling is
        // rejected at startup in STEP-006 (FullAtomicityViaInfrastructure is unsupported on this adapter).
        public TransactionScope CreateLocalTransaction(TransactionContext context)
            => null;

        public void Dispose()
            => _buffer?.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            _buffer?.Writer.TryComplete();
            return default;
        }
    }
}
