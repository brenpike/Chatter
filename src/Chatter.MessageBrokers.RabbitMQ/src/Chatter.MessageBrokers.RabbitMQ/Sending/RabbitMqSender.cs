using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
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

            // The SINGLE translation contract owns every core<->AMQP field mapping for the send boundary: native
            // frame fields (MessageId / ContentType / CorrelationId), the TTL->Expiration lift with the
            // TimeToLive-key drop, the dual-home CorrelationId header copy, and the marshalled header table.
            // Persistent=true is hardcoded inside the translator. The sender only resolves addressing (above) and
            // the ContentType fallback (the converter resolved for the configured MessageBodyType).
            var properties = RabbitMqMessageTranslator.ToAmqp(brokeredMessage, _bodyConverter.ContentType);

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
    }
}
