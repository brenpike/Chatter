namespace Chatter.MessageBrokers.RabbitMQ
{
    public static class RabbitMqMessageContext
    {
        public static readonly string RabbitMqBaseHeader = $"{MessageContext.ChatterBaseHeader}.RabbitMQ";
        public static readonly string InfrastructureType = $"{MessageContext.ChatterBaseHeader}.Infrastructure.RabbitMQ";

        /// <summary>
        /// The broker-assigned delivery tag identifying a message on its owning channel.
        /// </summary>
        public static readonly string DeliveryTag = $"{RabbitMqBaseHeader}.DeliveryTag";
        /// <summary>
        /// The epoch of the channel that delivered the message. Used to detect acknowledgements made against a stale channel.
        /// </summary>
        public static readonly string ChannelEpoch = $"{RabbitMqBaseHeader}.ChannelEpoch";
        /// <summary>
        /// Outbound publish-override only: the target exchange an outbound message is published to, set exclusively
        /// by <c>WithRabbitMqRouting(...)</c> and read by the sender at dispatch time. NEVER stamped from an inbound
        /// delivery — doing so would leak the inbound delivery's address into the outbound routing the core seeds
        /// from the inbound context, silently re-routing receive-then-send follow-ups.
        /// </summary>
        public static readonly string TargetExchange = $"{RabbitMqBaseHeader}.TargetExchange";
        /// <summary>
        /// Outbound publish-override only: the routing key an outbound message is published with, set exclusively by
        /// <c>WithRabbitMqRouting(...)</c> and read by the sender at dispatch time. NEVER stamped from an inbound
        /// delivery — doing so would leak the inbound delivery's address into the outbound routing the core seeds
        /// from the inbound context, silently re-routing receive-then-send follow-ups.
        /// </summary>
        public static readonly string RoutingKey = $"{RabbitMqBaseHeader}.RoutingKey";

        /// <summary>
        /// The message header carrying the classic-queue redelivery counter.
        /// </summary>
        public const string DeliveryCountHeader = "x-chatter-delivery-count";
    }
}
