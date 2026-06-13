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
        /// The target exchange a message is published to.
        /// </summary>
        public static readonly string TargetExchange = $"{RabbitMqBaseHeader}.TargetExchange";
        /// <summary>
        /// The routing key a message is published with.
        /// </summary>
        public static readonly string RoutingKey = $"{RabbitMqBaseHeader}.RoutingKey";

        /// <summary>
        /// The message header carrying the classic-queue redelivery counter.
        /// </summary>
        public const string DeliveryCountHeader = "x-chatter-delivery-count";
    }
}
