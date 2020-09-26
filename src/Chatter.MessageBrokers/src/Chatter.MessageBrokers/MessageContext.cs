namespace Chatter.MessageBrokers
{
    public class MessageContext
    {
        public static readonly string ChatterBaseHeader = "Chatter";
        private static readonly string TransactionBaseHeader = "Transaction";
        private static readonly string Saga = "Saga";
        private static readonly string Routing = "Routing";

        /// <summary>
        /// The receivers visited by the inbound message prior to the most recent message receiver
        /// </summary>
        public static readonly string Via = $"{ChatterBaseHeader}.Via";
        /// <summary>
        /// The reason the message failed to be received
        /// </summary>
        public static readonly string FailureDetails = $"{ChatterBaseHeader}.FailureDetails";
        /// <summary>
        /// The description of the failure causing the message not to be received
        /// </summary>
        public static readonly string FailureDescription = $"{ChatterBaseHeader}.FailureDescription";
        /// <summary>
        /// The mode of the transaction this message is participating in
        /// </summary>
        /// <remarks>
        /// The mode must be stored as a byte.
        /// </remarks>
        public static readonly string TransactionMode = $"{ChatterBaseHeader}.{TransactionBaseHeader}.Mode";
        /// <summary>
        /// The AMQP group this message is part of
        /// </summary>
        /// <remarks>
        /// Also known as session id in some messaging infrastructure implementations
        /// </remarks>
        public static readonly string GroupId = $"{ChatterBaseHeader}.GroupId";
        /// <summary>
        /// The subject of a message
        /// </summary>
        public static readonly string Subject = $"{ChatterBaseHeader}.Subject";
        /// <summary>
        /// The content type of a message's body
        /// </summary>
        public static readonly string ContentType = $"{ChatterBaseHeader}.ContentType";
        /// <summary>
        /// The correlation of a message
        /// </summary>
        public static readonly string CorrelationId = $"{ChatterBaseHeader}.CorrelationId";
        /// <summary>
        /// The time a message can live before no longer being valid
        /// </summary>
        public static readonly string TimeToLive = $"{ChatterBaseHeader}.TimeToLive";
        /// <summary>
        /// The Utc time the message will expire and no longer be valid
        /// </summary>
        public static readonly string ExpiryTimeUtc = $"{ChatterBaseHeader}.ExpiryTimeUtc";
        /// <summary>
        /// The status of the saga a message is part of
        /// </summary>
        public static readonly string SagaStatus = $"{ChatterBaseHeader}.{Saga}.Status";
        /// <summary>
        /// The Id of the saga a message is a part of
        /// </summary>
        public static readonly string SagaId = $"{ChatterBaseHeader}.{Saga}.Id";
        /// <summary>
        /// The path to the receiver that is performing the role of saga orchestrator
        /// </summary>
        public static readonly string SagaOrchestratorPath = $"{ChatterBaseHeader}.{Saga}.OrchestratorPath";
        /// <summary>
        /// True if the message has encountered an error while being received
        /// </summary>
        public static readonly string IsError = $"{ChatterBaseHeader}.IsError";
        /// <summary>
        /// The routing slip as json that describes how a message will be routed
        /// </summary>
        public static readonly string Slip = $"{ChatterBaseHeader}.{Routing}.Slip";
        /// <summary>
        /// The destination path of the message that invoked the current message broker receiver. Used to route a message to the same receiver(s).
        /// </summary>
        public static readonly string RouteToSelfPath = $"{ChatterBaseHeader}.{Routing}.RouteToSelfPath";
        /// <summary>
        /// The destination this message should reply to
        /// </summary>
        public static readonly string ReplyToAddress = $"{ChatterBaseHeader}.{Routing}.ReplyTo";
        /// <summary>
        /// The AMQP group this message should reply to
        /// </summary>
        /// <remarks>
        /// Also known as a session in some messaging infrastructure implementations
        /// </remarks>
        public static readonly string ReplyToGroupId = $"{ChatterBaseHeader}.{Routing}.ReplyToGroupId";

    }
}
