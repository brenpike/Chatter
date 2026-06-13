using Chatter.MessageBrokers;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Chatter.MessageBrokers.RabbitMQ
{
    /// <summary>
    /// The sole type-aware marshalling boundary between core <see cref="MessageContext"/> CLR values and the
    /// AMQP field-table wire types RabbitMQ.Client 7.2.1 can encode. Both publish and receive (and the
    /// republish on nack/deadletter) route their header bag through this type so an uncoerced value can never
    /// cross the wire boundary in either direction.
    /// </summary>
    /// <remarks>
    /// OUTBOUND (<see cref="ToHeaderTable"/>): the AMQP 0-9-1 field table the client can encode admits only
    /// <c>string</c>, <c>bool</c>, <c>sbyte</c>, <c>int</c>, <c>long</c>, <c>decimal</c>, <c>byte[]</c>, and a
    /// nested <c>IDictionary</c> — NOT <see cref="TimeSpan"/>, <see cref="DateTime"/>, <see cref="Guid"/>,
    /// <c>ulong</c>, <c>byte</c>, <c>uint</c>, or <c>ushort</c>. The core stamps several of those CLR types
    /// onto the context (most notably <see cref="MessageContext.TimeToLive"/> as a <see cref="TimeSpan"/> via
    /// <c>OutboundBrokeredMessage.WithTimeToLive</c>), so a raw copy of the context into the header table threw
    /// at publish. This boundary coerces each value to a table-legal form and never throws and never silently drops
    /// a populated value. TTL lifting is NOT owned here: the caller (the sender) lifts <see cref="MessageContext.TimeToLive"/>
    /// onto the native <see cref="BasicProperties.Expiration"/> from the core's authoritative
    /// <c>OutboundBrokeredMessage.GetTimeToLive()</c> (tolerant of a live <see cref="TimeSpan"/> AND an outbox-replayed
    /// string form), and the republish path re-applies the carried native Expiration; this boundary only DROPS the
    /// un-encodable <see cref="MessageContext.TimeToLive"/> key so it never reaches the table in any form.
    /// INBOUND (<see cref="ToContext"/>): a real broker delivers a string application header as an AMQP longstr,
    /// which RabbitMQ.Client surfaces as a <c>byte[]</c>. The core casts a fixed set of string-typed context keys
    /// straight to <c>string</c> (e.g. <c>InboundBrokeredMessage</c> casts <see cref="MessageContext.CorrelationId"/>
    /// at construction), so a raw copy of the received headers left those keys as <c>byte[]</c> and the unguarded
    /// <c>(string)</c> cast threw <see cref="InvalidCastException"/> before the handler ran. This boundary decodes
    /// the known string-typed keys from <c>byte[]</c> to <c>string</c> while preserving every unknown key verbatim,
    /// so genuine binary headers are never corrupted and the numeric delivery-count path (owned by
    /// <c>RabbitMqReceiver.ReadHeaderAsLong</c>) is left untouched.
    /// </remarks>
    internal static class RabbitMqHeaderMarshaller
    {
        // INVARIANT: the core context keys whose values the inbound receive path casts straight to (string)
        // (InboundBrokeredMessage.CorrelationId / Via, OutboxProcessor ContentType, the routing reads of
        // RouteToSelfPath / ReplyTo* / RoutingSlip, and the failure-detail strings). A real broker surfaces a
        // string application header as a byte[] (AMQP longstr), so each of these must be decoded back to string
        // before the unguarded cast runs. Maintained as a documented constant set so adding a string-typed core
        // key is a single edit here. InfrastructureType is included for consistency even though the receiver
        // overwrites it with a fresh stamp.
        private static readonly HashSet<string> _knownStringTypedKeys = new HashSet<string>
        {
            MessageContext.CorrelationId,
            MessageContext.ContentType,
            MessageContext.Subject,
            MessageContext.GroupId,
            MessageContext.Via,
            MessageContext.RouteToSelfPath,
            MessageContext.ReplyToAddress,
            MessageContext.ReplyToGroupId,
            MessageContext.RoutingSlip,
            MessageContext.FailureDetails,
            MessageContext.FailureDescription,
            MessageContext.InfrastructureType
        };

        /// <summary>
        /// Projects an outbound <see cref="MessageContext"/> dictionary into an AMQP-field-table-legal header bag,
        /// dropping the un-encodable <see cref="MessageContext.TimeToLive"/> key (TTL lifting onto the native
        /// <paramref name="properties"/>.Expiration is owned by the caller via
        /// <c>OutboundBrokeredMessage.GetTimeToLive()</c>). Never throws on an unencodable value and never silently
        /// drops a populated value. <paramref name="properties"/> is retained for parity with the republish call site
        /// and for any future native-property lift.
        /// </summary>
        public static IDictionary<string, object> ToHeaderTable(IEnumerable<KeyValuePair<string, object>> context, BasicProperties properties)
        {
            var table = new Dictionary<string, object>();
            if (context == null)
            {
                return table;
            }

            foreach (var entry in context)
            {
                // TimeToLive is a TimeSpan the field table cannot encode (and an outbox-replayed context carries it as
                // a string). The native Expiration lift is owned by the caller via OutboundBrokeredMessage.GetTimeToLive()
                // (the republish path re-applies the carried native Expiration), so this boundary UNCONDITIONALLY DROPS
                // the key in ANY form so it never reaches the table.
                if (entry.Key == MessageContext.TimeToLive)
                {
                    continue;
                }

                // ExpiryTimeUtc is a DateTime the field table cannot encode; emit it as a round-trippable ISO-8601
                // ("O") string in the table (it has no native AMQP property the way TimeToLive does).
                if (entry.Key == MessageContext.ExpiryTimeUtc && entry.Value is DateTime expiry)
                {
                    table[entry.Key] = expiry.ToString("O", CultureInfo.InvariantCulture);
                    continue;
                }

                table[entry.Key] = CoerceOutboundValue(entry.Value);
            }

            // Drop any null-coerced entries: a null value is not field-table-legal, and the core treats an absent
            // key the same as a null one.
            var nullKeys = new List<string>();
            foreach (var entry in table)
            {
                if (entry.Value == null)
                {
                    nullKeys.Add(entry.Key);
                }
            }
            foreach (var key in nullKeys)
            {
                table.Remove(key);
            }

            return table;
        }

        // Coerce a single CLR value to a field-table-legal form. Returns null for a null input (the caller drops
        // the key). Never throws: an unknown CLR type falls back to its invariant string form so a populated
        // value always survives the hop in SOME form rather than faulting the publish.
        private static object CoerceOutboundValue(object value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string:
                case bool:
                case sbyte:
                case int:
                case long:
                case decimal:
                case byte[]:
                    return value;
                case ulong asULong:
                    // Widen to long when it fits; an out-of-range ulong falls back to its invariant string form.
                    return asULong <= long.MaxValue ? (long)asULong : asULong.ToString(CultureInfo.InvariantCulture);
                case uint asUInt:
                    return (long)asUInt;
                case ushort asUShort:
                    return (long)asUShort;
                case byte asByte:
                    return (long)asByte;
                case short asShort:
                    return (int)asShort;
                case Guid asGuid:
                    return asGuid.ToString();
                default:
                    // Documented catch-all: any other unencodable CLR type (DateTime not handled above, enums,
                    // etc.) is rendered to its invariant string form so the publish never throws and the value is
                    // never silently dropped.
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Projects a received AMQP header bag into a core-ready context dictionary: known string-typed keys that
        /// arrived as a <c>byte[]</c> (AMQP longstr) are decoded to <c>string</c> so the core's unguarded cast
        /// holds; every other key is preserved verbatim so genuine binary headers and the numeric delivery-count
        /// path are untouched.
        /// </summary>
        public static IDictionary<string, object> ToContext(IEnumerable<KeyValuePair<string, object>> headers)
        {
            var context = new Dictionary<string, object>();
            if (headers == null)
            {
                return context;
            }

            foreach (var entry in headers)
            {
                if (_knownStringTypedKeys.Contains(entry.Key))
                {
                    context[entry.Key] = DecodeStringTypedValue(entry.Value);
                    continue;
                }

                // UNKNOWN keys: preserve verbatim. Force-decoding an unknown byte[] would corrupt a genuine binary
                // header, and the numeric delivery-count tolerance is owned by RabbitMqReceiver.ReadHeaderAsLong.
                context[entry.Key] = entry.Value;
            }

            return context;
        }

        // A string-typed key's value arrives as a byte[] (longstr) from a real broker or as a string from an
        // in-process double; decode the byte[] as UTF-8 and pass a string through unchanged. Any other type (e.g.
        // null) is preserved as-is rather than coerced.
        private static object DecodeStringTypedValue(object value)
        {
            switch (value)
            {
                case byte[] asBytes:
                    return Encoding.UTF8.GetString(asBytes);
                case string asString:
                    return asString;
                default:
                    return value;
            }
        }
    }
}
