using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.RabbitMQ.Sending
{
    /// <summary>
    /// The RabbitMQ <see cref="IMessagingInfrastructureDispatcher"/>. Publishes each outbound message on a
    /// pooled publish channel (publisher confirms enabled) rented from <see cref="IRabbitMqConnectionSource"/>,
    /// never the serialized receive channel. Addressing follows the default-exchange convention — exchange
    /// <c>""</c> with routing key = <see cref="OutboundBrokeredMessage.Destination"/> (the queue name) — unless
    /// the message carries a <see cref="RabbitMqMessageContext.TargetExchange"/> / <see cref="RabbitMqMessageContext.RoutingKey"/>
    /// override stamped via <c>WithRabbitMqRouting(...)</c>.
    /// </summary>
    /// <remarks>
    /// INVARIANT: publish channels run in confirm mode (<see cref="IRabbitMqConnectionSource.AcquirePublishChannelAsync"/>)
    /// with publisher-confirmation tracking, so the awaited <see cref="IChannel.BasicPublishAsync{TProperties}(string, string, bool, TProperties, ReadOnlyMemory{byte}, CancellationToken)"/>
    /// only completes once the broker confirms the publish; an unconfirmed publish faults the returned task and
    /// propagates (no silent loss). INVARIANT: <c>mandatory: true</c> so an unroutable message surfaces a return
    /// rather than being silently dropped.
    /// </remarks>
    public sealed class RabbitMqSender : IMessagingInfrastructureDispatcher
    {
        private readonly IRabbitMqConnectionSource _connectionSource;
        private readonly IBrokeredMessageBodyConverter _bodyConverter;
        private readonly ILogger<RabbitMqSender> _logger;

        public RabbitMqSender(IRabbitMqConnectionSource connectionSource,
                              IBodyConverterFactory bodyConverterFactory,
                              RabbitMqOptions rabbitOptions,
                              ILogger<RabbitMqSender> logger)
        {
            _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
            if (bodyConverterFactory is null)
            {
                throw new ArgumentNullException(nameof(bodyConverterFactory));
            }

            if (rabbitOptions is null)
            {
                throw new ArgumentNullException(nameof(rabbitOptions));
            }

            // MessageBodyType is fixed per options, so resolve the converter once here; the resolved converter
            // governs BOTH the bytes written and the advertised ContentType so the option can no longer be ignored.
            _bodyConverter = bodyConverterFactory.CreateBodyConverter(rabbitOptions.MessageBodyType);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
        {
            if (brokeredMessages is null)
            {
                throw new ArgumentNullException(nameof(brokeredMessages));
            }

            foreach (var brokeredMessage in brokeredMessages)
            {
                await Dispatch(brokeredMessage, transactionContext).ConfigureAwait(false);
            }
        }

        public async Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
        {
            if (brokeredMessage is null)
            {
                throw new ArgumentNullException(nameof(brokeredMessage));
            }

            var (exchange, routingKey) = ResolveAddress(brokeredMessage);
            _logger.LogTrace("Publishing brokered message to exchange '{exchange}' with routing key '{routingKey}'.", exchange, routingKey);

            var properties = BuildProperties(brokeredMessage);

            await using var rental = await _connectionSource.AcquirePublishChannelAsync(CancellationToken.None).ConfigureAwait(false);

            // RabbitMQ.Client 7.2.1 guarantee relied upon here: the rented channel is created with
            // publisherConfirmationsEnabled + publisherConfirmationTrackingEnabled (see
            // RabbitMqConnectionSource.CreatePublishChannelAsync). With tracking enabled, an unroutable
            // mandatory publish triggers a basic.return from the broker; the client correlates the return
            // to this publish by publish-sequence-number via HandleReturn -> HandleNack(isReturn:true) ->
            // tcs.SetException(PublishException) — no BasicReturnAsync handler is needed. The bare await
            // therefore faults with PublishException and propagates as a Dispatch failure: no silent loss.
            // If RabbitMQ.Client is upgraded past 7.2.1, or if CreatePublishChannelAsync's
            // CreateChannelOptions change, re-verify that this fault-on-return guarantee still holds.
            await rental.Channel.BasicPublishAsync(exchange: exchange,
                                                   routingKey: routingKey,
                                                   mandatory: true,
                                                   basicProperties: properties,
                                                   body: new ReadOnlyMemory<byte>(brokeredMessage.Body),
                                                   cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        // Default-exchange convention: publish to exchange "" with routing key = Destination (the queue name),
        // unless the message carries an explicit exchange + routing key override stamped via WithRabbitMqRouting.
        private static (string exchange, string routingKey) ResolveAddress(OutboundBrokeredMessage brokeredMessage)
        {
            var targetExchange = brokeredMessage.GetMessageContextByKey<string>(RabbitMqMessageContext.TargetExchange);
            var routingKeyOverride = brokeredMessage.GetMessageContextByKey<string>(RabbitMqMessageContext.RoutingKey);

            if (targetExchange != null || !string.IsNullOrWhiteSpace(routingKeyOverride))
            {
                return (targetExchange ?? string.Empty,
                        string.IsNullOrWhiteSpace(routingKeyOverride) ? brokeredMessage.Destination : routingKeyOverride);
            }

            return (string.Empty, brokeredMessage.Destination);
        }

        private BasicProperties BuildProperties(OutboundBrokeredMessage brokeredMessage)
        {
            var properties = new BasicProperties
            {
                // Durable delivery so a message survives a broker restart on a durable queue.
                Persistent = true,
                // Advertise the content type the core ACTUALLY stamped on the context when it serialized the body
                // (OutboundBrokeredMessage's ctor stamps MessageContext[ContentType] = converter.ContentType), so a
                // live message and an outbox-replayed one both advertise the same content type. Fall back to the
                // sender's resolved converter only when the stamp is absent (e.g. a context built without a converter).
                ContentType = ResolveContentType(brokeredMessage)
            };

            // INVARIANT: the context is never raw-copied onto the field table — the marshaller is the sole boundary
            // that coerces each value to a table-legal type and drops the un-encodable TimeToLive key. The native
            // Expiration is now lifted HERE from the core's authoritative OutboundBrokeredMessage.GetTimeToLive()
            // (tolerant of a live TimeSpan AND an outbox-replayed string form) rather than re-interpreted by the
            // marshaller, so a string-form TTL is no longer missed.
            properties.Headers = RabbitMqHeaderMarshaller.ToHeaderTable(brokeredMessage.MessageContext, properties);

            var expiration = ResolveExpiration(brokeredMessage);
            if (expiration != null)
            {
                properties.Expiration = expiration;
            }

            if (!string.IsNullOrWhiteSpace(brokeredMessage.MessageId))
            {
                properties.MessageId = brokeredMessage.MessageId;
            }

            if (!string.IsNullOrWhiteSpace(brokeredMessage.CorrelationId))
            {
                properties.CorrelationId = brokeredMessage.CorrelationId;
            }

            return properties;
        }

        // Source the advertised content type from the actual-serialization stamp the core wrote onto the context
        // (MessageContext.ContentType), falling back to the sender's resolved converter when that stamp is missing.
        private string ResolveContentType(OutboundBrokeredMessage brokeredMessage)
        {
            var stampedContentType = brokeredMessage.GetMessageContextByKey<string>(MessageContext.ContentType);
            return string.IsNullOrWhiteSpace(stampedContentType) ? _bodyConverter.ContentType : stampedContentType;
        }

        // Lift TimeToLive onto the native Expiration (ms string) from the core's authoritative accessor, which is
        // tolerant of both a live TimeSpan and an outbox-replayed string form. A non-positive TTL floors at "0";
        // fractional milliseconds are floored by the (long) truncation. Null TimeToLive leaves Expiration unset.
        private static string ResolveExpiration(OutboundBrokeredMessage brokeredMessage)
        {
            var timeToLive = brokeredMessage.GetTimeToLive();
            if (timeToLive == null)
            {
                return null;
            }

            var milliseconds = timeToLive.Value.TotalMilliseconds <= 0d ? 0L : (long)timeToLive.Value.TotalMilliseconds;
            return milliseconds.ToString(CultureInfo.InvariantCulture);
        }
    }
}
