using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;

namespace Chatter.CQRS.Context
{
    public static class MessageHandlerContextExtensions
    {
        /// <summary>
        /// Marks the outbound brokered message on this context to be dispatched through the RabbitMQ
        /// infrastructure by stamping <see cref="RabbitMqMessageContext.InfrastructureType"/> as the target
        /// infrastructure type.
        /// </summary>
        public static IMessageBrokerContext RabbitMq(this IMessageHandlerContext context)
        {
            if (context is IMessageBrokerContext mbc)
            {
                mbc.BrokeredMessage?.UseMessagingInfrastructure(_ => RabbitMqMessageContext.InfrastructureType);
                return mbc;
            }

            return null;
        }

        /// <summary>
        /// Overrides the default-exchange convention for an outbound message: publishes to the supplied
        /// <paramref name="exchange"/> with the supplied <paramref name="routingKey"/> instead of exchange
        /// <c>""</c> with routing key = Destination. The values are stamped into the message context as
        /// <see cref="RabbitMqMessageContext.TargetExchange"/> and <see cref="RabbitMqMessageContext.RoutingKey"/>,
        /// which the sender reads at dispatch time.
        /// </summary>
        public static OutboundBrokeredMessage WithRabbitMqRouting(this OutboundBrokeredMessage outboundBrokeredMessage, string exchange, string routingKey)
        {
            outboundBrokeredMessage.MessageContext[RabbitMqMessageContext.TargetExchange] = exchange;
            outboundBrokeredMessage.MessageContext[RabbitMqMessageContext.RoutingKey] = routingKey;
            return outboundBrokeredMessage;
        }

        /// <summary>
        /// Overrides the default-exchange convention for the <c>context.RabbitMq().Send(..., options)</c> handler
        /// path: stamps <see cref="RabbitMqMessageContext.TargetExchange"/> and
        /// <see cref="RabbitMqMessageContext.RoutingKey"/> into <paramref name="options"/>. The core dispatcher
        /// merges the options' message context last into the dispatched <see cref="OutboundBrokeredMessage"/>,
        /// which the sender reads at dispatch time. An empty or blank <paramref name="exchange"/> selects the
        /// default exchange with a custom routing key, matching the behaviour of the
        /// <see cref="WithRabbitMqRouting(OutboundBrokeredMessage, string, string)"/> overload.
        /// </summary>
        public static SendOptions WithRabbitMqRouting(this SendOptions options, string exchange, string routingKey)
        {
            options.WithMessageContext(RabbitMqMessageContext.TargetExchange, exchange);
            options.WithMessageContext(RabbitMqMessageContext.RoutingKey, routingKey);
            return options;
        }

        /// <summary>
        /// Overrides the default-exchange convention for the <c>context.RabbitMq().Publish(..., options)</c>
        /// handler path: stamps <see cref="RabbitMqMessageContext.TargetExchange"/> and
        /// <see cref="RabbitMqMessageContext.RoutingKey"/> into <paramref name="options"/>. The core dispatcher
        /// merges the options' message context last into the dispatched <see cref="OutboundBrokeredMessage"/>,
        /// which the sender reads at dispatch time. An empty or blank <paramref name="exchange"/> selects the
        /// default exchange with a custom routing key, matching the behaviour of the
        /// <see cref="WithRabbitMqRouting(OutboundBrokeredMessage, string, string)"/> overload.
        /// </summary>
        public static PublishOptions WithRabbitMqRouting(this PublishOptions options, string exchange, string routingKey)
        {
            options.WithMessageContext(RabbitMqMessageContext.TargetExchange, exchange);
            options.WithMessageContext(RabbitMqMessageContext.RoutingKey, routingKey);
            return options;
        }
    }
}
