using Chatter.MessageBrokers.Context;
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
        private readonly RabbitMqBodyConverter _bodyConverter;
        private readonly ILogger<RabbitMqSender> _logger;

        public RabbitMqSender(IRabbitMqConnectionSource connectionSource,
                              RabbitMqBodyConverter bodyConverter,
                              ILogger<RabbitMqSender> logger)
        {
            _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
            _bodyConverter = bodyConverter ?? throw new ArgumentNullException(nameof(bodyConverter));
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

            // The rented channel has publisher confirms (with tracking) enabled, so this await only completes once
            // the broker confirms the publish; an unconfirmed publish faults here and propagates. mandatory:true
            // surfaces an unroutable return rather than silently dropping the message.
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
                ContentType = _bodyConverter.ContentType,
                Headers = new Dictionary<string, object>(brokeredMessage.MessageContext)
            };

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
    }
}
