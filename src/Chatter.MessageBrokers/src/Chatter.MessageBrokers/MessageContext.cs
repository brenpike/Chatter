using System.Text.Json;

namespace Chatter.MessageBrokers
{
    public class MessageContext
    {
        /// <summary>
        /// Materializes a persisted MessageContext value (a System.Text.Json <see cref="JsonElement"/>)
        /// back to the CLR type that Newtonsoft's untyped <c>IDictionary&lt;string, object&gt;</c>
        /// deserialization would have produced. The outbox persists MessageContext as JSON whose values
        /// are heterogeneously typed (string headers, a numeric ReceiveAttempts, a DateTime scheduled
        /// enqueue time), and downstream typed readers depend on those CLR types being restored on replay.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this is a deserialize-only companion to the header constants below — it must
        /// reproduce Newtonsoft's default untyped read semantics so that the (string), (DateTime?), and
        /// integer reads on the replayed context continue to hold:
        /// <list type="bullet">
        /// <item>Number -> <c>long</c> when it fits an Int64, else <c>double</c> (matches Newtonsoft).</item>
        /// <item>String -> <see cref="System.DateTime"/> when it is a strict ISO-8601 value
        /// (<see cref="JsonElement.TryGetDateTime"/>, matching Newtonsoft's default DateParseHandling),
        /// else the raw string. TryGetDateTime is strict by design so non-date strings are not over-coerced.</item>
        /// <item>True/False -> <c>bool</c>.</item>
        /// <item>Null/Undefined -> <c>null</c>.</item>
        /// <item>Object/Array -> raw JSON text so no value is dropped.</item>
        /// </list>
        /// </remarks>
        internal static object MaterializePersistedContextValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var asLong) ? asLong : element.GetDouble();
                case JsonValueKind.String:
                    return element.TryGetDateTime(out var asDateTime) ? asDateTime : (object)element.GetString();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetBoolean();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                default:
                    return element.GetRawText();
            }
        }

        public static readonly string ChatterBaseHeader = "Chatter";
        private static readonly string Routing = "Routing";
        private static readonly string Infrastructure = "Infrastructure";

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
        /// True if the message has encountered an error while being received
        /// </summary>
        public static readonly string IsError = $"{ChatterBaseHeader}.IsError";
        /// <summary>
        /// The routing slip as json that describes how a message will be routed
        /// </summary>
        public static readonly string RoutingSlip = $"{ChatterBaseHeader}.{Routing}.Slip";
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
        /// <summary>
        /// The type of brokered message infrastructure the message is being sent or received on
        /// </summary>
        public static readonly string InfrastructureType = $"{ChatterBaseHeader}.{Infrastructure}.Type";
        /// <summary>
        /// The total number of attempts that have been made by a receiver to receive and handle the message
        /// </summary>
        public static readonly string ReceiveAttempts = $"{ChatterBaseHeader}.{Infrastructure}.ReceiveAttempts";
    }
}
