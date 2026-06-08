using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Chatter.MessageBrokers
{
    public class MessageContext
    {
        /// <summary>
        /// Deserializes a persisted MessageContext JSON object into a fully-materialized
        /// <c>IDictionary&lt;string, object&gt;</c> whose values carry the CLR types Newtonsoft's untyped
        /// read would have produced. A thin wrapper over a <see cref="ChatterJson.Options"/> deserialize:
        /// the global <c>MaterializingObjectConverter</c> registered there materializes every object-typed
        /// value inline through the shared <see cref="MaterializeJsonElement"/> recipe, so no downstream
        /// typed reader observes a raw <see cref="JsonElement"/>.
        /// </summary>
        /// <remarks>
        /// INVARIANT: materialization is driven by the registered converter (same shared recipe), so the
        /// per-value parity semantics documented on <see cref="MaterializePersistedContextValue"/> hold
        /// uniformly across all seams. Returns an empty dictionary for null/empty/whitespace json.
        /// </remarks>
        internal static IDictionary<string, object> MaterializePersistedContext(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, object>();
            }

            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, ChatterJson.Options);
        }

        /// <summary>
        /// Projects an already-deserialized System.Text.Json context dictionary (object-typed values that
        /// are raw <see cref="JsonElement"/>s, as produced when STJ deserializes an
        /// <c>IDictionary&lt;string, object&gt;</c>) into a fully-materialized dictionary whose values
        /// carry the CLR types Newtonsoft's untyped read would have produced. Values that are not
        /// JsonElements (already CLR-typed) are passed through unchanged. Returns an empty dictionary for
        /// a null source.
        /// </summary>
        internal static IDictionary<string, object> MaterializePersistedContext(IDictionary<string, object> context)
        {
            if (context == null)
            {
                return new Dictionary<string, object>();
            }

            return context.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is JsonElement element ? MaterializePersistedContextValue(element) : kvp.Value);
        }

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
        /// <item>Object -> a recursively-materialized <c>Dictionary&lt;string, object&gt;</c> and
        /// Array -> a recursively-materialized <c>List&lt;object&gt;</c>, so structured attachment
        /// values stay navigable as nested CLR collections (the closest CLR-fidelity match to the
        /// <c>JObject</c>/<c>JArray</c> Newtonsoft's untyped read produced) rather than being
        /// flattened to a raw JSON string. Each nested value/element is projected through this same
        /// method so the per-value parity semantics hold uniformly at every depth.</item>
        /// </list>
        /// </remarks>
        internal static object MaterializePersistedContextValue(JsonElement element)
            => MaterializeJsonElement(element);

        /// <summary>
        /// The single shared materialization recipe: maps a System.Text.Json <see cref="JsonElement"/>
        /// to the CLR type Newtonsoft's untyped <c>IDictionary&lt;string, object&gt;</c> read would have
        /// produced. This is the one construction point invoked both by <see cref="MaterializePersistedContext"/>
        /// (via <see cref="MaterializePersistedContextValue"/>) and by the global
        /// <c>MaterializingObjectConverter</c> registered on <see cref="ChatterJson.Options"/>, so the
        /// per-value parity semantics documented on <see cref="MaterializePersistedContextValue"/> hold
        /// identically at every object-typed read position and at every nesting depth.
        /// </summary>
        internal static object MaterializeJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var asLong) ? (object)asLong : (object)element.GetDouble();
                case JsonValueKind.String:
                    return element.TryGetDateTime(out var asDateTime) ? asDateTime : (object)element.GetString();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetBoolean();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.Object:
                    // Last-write-wins on duplicate property names, matching Newtonsoft's default untyped
                    // read and System.Text.Json's own Dictionary<string, object> converter. A LINQ
                    // ToDictionary would throw ArgumentException on duplicate keys — duplicate keys are
                    // legal JSON (RFC 8259), so an inbound/persisted object-typed value carrying them must
                    // not poison the deserialize at any object-typed seam this recipe now backs.
                    var materializedObject = new Dictionary<string, object>();
                    foreach (var property in element.EnumerateObject())
                    {
                        materializedObject[property.Name] = MaterializeJsonElement(property.Value);
                    }
                    return materializedObject;
                case JsonValueKind.Array:
                    return element.EnumerateArray()
                        .Select(MaterializeJsonElement)
                        .ToList();
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
