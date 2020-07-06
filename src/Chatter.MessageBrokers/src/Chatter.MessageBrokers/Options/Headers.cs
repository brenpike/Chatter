namespace Chatter.MessageBrokers.Options
{
    public class Headers
    {
        public static readonly string ChatterBaseHeader = "Chatter";
        private static readonly string ReplyBaseHeader = "Reply";
        private static readonly string TransactionBaseHeader = "Transaction";
        private static readonly string Saga = "Saga";

        public static readonly string Via = $"{ChatterBaseHeader}.Via";

        public static readonly string FailureDetails = $"{ChatterBaseHeader}.FailureDetails";
        public static readonly string FailureDescription = $"{ChatterBaseHeader}.FailureDescription";

        public static readonly string TransactionMode = $"{ChatterBaseHeader}.{TransactionBaseHeader}.Mode";

        public static readonly string ReplyTo = $"{ChatterBaseHeader}.{ReplyBaseHeader}.ReplyTo";
        public static readonly string ReplyToGroupId = $"{ChatterBaseHeader}.{ReplyBaseHeader}.ReplyToGroupId";

        public static readonly string GroupId = $"{ChatterBaseHeader}.GroupId";
        public static readonly string Subject = $"{ChatterBaseHeader}.Subject";
        public static readonly string ContentType = $"{ChatterBaseHeader}.ContentType";
        public static readonly string CorrelationId = $"{ChatterBaseHeader}.CorrelationId";
        public static readonly string TimeToLive = $"{ChatterBaseHeader}.TimeToLive";
        public static readonly string ExpiryTimeUtc = $"{ChatterBaseHeader}.ExpiryTimeUtc";

        public static readonly string SagaStatus = $"{ChatterBaseHeader}.{Saga}.Status";
        public static readonly string SagaId = $"{ChatterBaseHeader}.{Saga}.Id";

        public static readonly string IsError = $"{ChatterBaseHeader}.IsError";
    }
}
