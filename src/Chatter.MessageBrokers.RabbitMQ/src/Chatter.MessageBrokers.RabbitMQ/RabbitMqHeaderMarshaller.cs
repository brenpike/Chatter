using Chatter.MessageBrokers;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Chatter.MessageBrokers.RabbitMQ
{
    /// <summary>
    /// The header-coercion HELPER that <see cref="RabbitMqMessageTranslator"/>'s header-home fields delegate to.
    /// Coerces each core <see cref="MessageContext"/> CLR value to an AMQP-field-table-legal wire type outbound
    /// (<see cref="ToHeaderTable"/>) and rehydrates a known core header key back to its ORIGINAL CLR type inbound
    /// (<see cref="ToContext"/>), so an uncoerced value can never cross the header boundary in either direction.
    /// The translator owns the native-frame fields and routes every header bag through this helper; this type is
    /// no longer a standalone boundary but the header arm of the single translation contract.
    /// </summary>
    /// <remarks>
    /// Each known core header key is governed by a SINGLE <see cref="HeaderCoercion"/> descriptor carrying a
    /// SYMMETRIC bidirectional coercion: <see cref="HeaderCoercion.Encode"/> maps the core CLR value to a
    /// field-table-legal wire form outbound AND <see cref="HeaderCoercion.Decode"/> maps the received wire value
    /// back to the SAME original CLR type inbound. The pairing is the closed-by-construction invariant: a non-string
    /// core key (e.g. <see cref="MessageContext.ExpiryTimeUtc"/>, a <see cref="DateTime"/>) cannot be added to the
    /// table with an encode but no matching decode, so it necessarily rehydrates to its original CLR type after a
    /// round trip and the core's typed cast on it (e.g. <c>OutboundBrokeredMessage.RefreshTimeToLive</c>'s
    /// <c>(DateTime?)</c> cast on ExpiryTimeUtc) holds — closing the asymmetry class that left an encoded-only
    /// non-string key as a <c>byte[]</c>/<c>string</c> on receive.
    /// </remarks>
    /// <remarks>
    /// OUTBOUND (<see cref="ToHeaderTable"/>): the AMQP 0-9-1 field table the client can encode admits only
    /// <c>string</c>, <c>bool</c>, <c>sbyte</c>, <c>int</c>, <c>long</c>, <c>decimal</c>, <c>byte[]</c>, and a
    /// nested <c>IDictionary</c> — NOT <see cref="TimeSpan"/>, <see cref="DateTime"/>, <see cref="Guid"/>,
    /// <c>ulong</c>, <c>byte</c>, <c>uint</c>, or <c>ushort</c>. The core stamps several of those CLR types
    /// onto the context (most notably <see cref="MessageContext.TimeToLive"/> as a <see cref="TimeSpan"/> via
    /// <c>OutboundBrokeredMessage.WithTimeToLive</c>), so a raw copy of the context into the header table threw
    /// at publish. This helper coerces each value to a table-legal form and never throws and never silently drops
    /// a populated value. TTL lifting is NOT owned here: the translator lifts <see cref="MessageContext.TimeToLive"/>
    /// onto the native <see cref="BasicProperties.Expiration"/> from the core's authoritative
    /// <c>OutboundBrokeredMessage.GetTimeToLive()</c>; this helper only DROPS the un-encodable
    /// <see cref="MessageContext.TimeToLive"/> key so it never reaches the table in any form.
    /// INBOUND (<see cref="ToContext"/>): a real broker delivers a string application header as an AMQP longstr,
    /// which RabbitMQ.Client surfaces as a <c>byte[]</c>, and a non-string core key encoded outbound (ExpiryTimeUtc
    /// as an ISO-8601 string) likewise arrives as a <c>byte[]</c>/<c>string</c>. The core casts each known core key
    /// straight to its expected CLR type (e.g. <c>InboundBrokeredMessage</c> casts <see cref="MessageContext.Via"/>
    /// to <c>string</c>; <c>OutboundBrokeredMessage.RefreshTimeToLive</c> casts ExpiryTimeUtc to <c>(DateTime?)</c>),
    /// so a raw copy of the received headers left those keys mistyped and the unguarded cast threw
    /// <see cref="InvalidCastException"/> before the handler ran. This helper rehydrates each known core header key
    /// to its ORIGINAL CLR type via that key's symmetric coercion Decode (string-typed keys decode to <c>string</c>;
    /// ExpiryTimeUtc decodes to <c>DateTime</c>) while preserving every unknown key verbatim, so genuine binary
    /// headers are never corrupted and the numeric delivery-count path (owned by
    /// <c>RabbitMqReceiver.ReadHeaderAsLong</c>) is left untouched. CorrelationId and ContentType are NOT in this
    /// set: the translator owns them as native-frame fields and reads them off the frame inbound, so a decoded
    /// header copy is not the inbound source for them.
    /// </remarks>
    internal static class RabbitMqHeaderMarshaller
    {
        // A SYMMETRIC bidirectional coercion for one known core header key: Encode maps the core CLR value to a
        // field-table-legal wire form outbound, Decode maps the received wire value back to the SAME original CLR
        // type inbound. INVARIANT: the two arms are paired by construction — a key cannot be added with an encode
        // but no matching decode, so any non-string core header key necessarily rehydrates to its original CLR type
        // after a round trip (closing the encoded-only-then-uncast asymmetry class).
        private sealed class HeaderCoercion
        {
            public HeaderCoercion(Func<object, object> encode, Func<object, object> decode)
            {
                Encode = encode;
                Decode = decode;
            }

            // CLR -> field-table-legal wire form (outbound). Never throws; returns null to drop the key.
            public Func<object, object> Encode { get; }
            // Wire value -> the SAME original CLR type (inbound). Never throws; returns null to drop the key.
            public Func<object, object> Decode { get; }
        }

        // INVARIANT: the core context keys that have a HEADER home, each governed by ONE symmetric coercion. The ten
        // string-typed routing/failure keys (Via / RouteToSelfPath / ReplyTo* / RoutingSlip / GroupId / Subject /
        // the failure-detail strings + InfrastructureType) encode string-identity outbound and decode byte[]/string
        // -> string inbound, because the inbound receive path casts them straight to (string) and a real broker
        // surfaces a string application header as a byte[] (AMQP longstr). ExpiryTimeUtc is a non-string (DateTime)
        // core key: it encodes DateTime -> ISO-8601 ("O") string outbound and decodes the wire value back to a
        // DateTime inbound, so the core's (DateTime?) cast in RefreshTimeToLive holds after a round trip.
        // CorrelationId and ContentType are DELIBERATELY ABSENT: RabbitMqMessageTranslator owns them as native-frame
        // fields (it sets the native frame outbound and reads the native frame inbound), so the header copy is not
        // their decode source. This is the single header-key coercion declaration; a non-string header key cannot be
        // added without its decode (the symmetric pairing is enforced by the descriptor shape).
        // The shared string-typed coercion the ten routing/failure keys use: encode = the general outbound coercion
        // (a string passes through identity), decode = byte[]/string -> string. Behaviour-preserving vs the prior
        // _stringTypedHeaderKeys HashSet (CoerceOutboundValue outbound, DecodeStringTypedValue inbound). Declared
        // BEFORE _headerCoercions so the static field initializers (run in textual order) see it populated.
        private static readonly HeaderCoercion StringTypedCoercion =
            new HeaderCoercion(CoerceOutboundValue, DecodeStringTypedValue);

        private static readonly IReadOnlyDictionary<string, HeaderCoercion> _headerCoercions =
            new Dictionary<string, HeaderCoercion>
            {
                [MessageContext.Subject] = StringTypedCoercion,
                [MessageContext.GroupId] = StringTypedCoercion,
                [MessageContext.Via] = StringTypedCoercion,
                [MessageContext.RouteToSelfPath] = StringTypedCoercion,
                [MessageContext.ReplyToAddress] = StringTypedCoercion,
                [MessageContext.ReplyToGroupId] = StringTypedCoercion,
                [MessageContext.RoutingSlip] = StringTypedCoercion,
                [MessageContext.FailureDetails] = StringTypedCoercion,
                [MessageContext.FailureDescription] = StringTypedCoercion,
                [MessageContext.InfrastructureType] = StringTypedCoercion,
                [MessageContext.ExpiryTimeUtc] = new HeaderCoercion(EncodeExpiryTimeUtc, DecodeExpiryTimeUtc)
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

                // A known core header key routes through its symmetric coercion's Encode (e.g. ExpiryTimeUtc's
                // DateTime -> ISO-8601 ("O") string); every other key falls through to the general outbound coercion.
                table[entry.Key] = _headerCoercions.TryGetValue(entry.Key, out var coercion)
                    ? coercion.Encode(entry.Value)
                    : CoerceOutboundValue(entry.Value);
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
        /// Projects a received AMQP header bag into a core-ready context dictionary: a known core header key is
        /// rehydrated to its ORIGINAL CLR type via its symmetric coercion's Decode (a string-typed key arriving as a
        /// <c>byte[]</c> AMQP longstr decodes to <c>string</c>; ExpiryTimeUtc decodes back to a <c>DateTime</c>) so
        /// the core's typed cast holds; every other key is preserved verbatim so genuine binary headers and the
        /// numeric delivery-count path are untouched. A coercion whose Decode returns null (e.g. a malformed
        /// ExpiryTimeUtc that cannot parse) DROPS the key so the core's null-guard short-circuits cleanly.
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
                if (_headerCoercions.TryGetValue(entry.Key, out var coercion))
                {
                    var decoded = coercion.Decode(entry.Value);
                    if (decoded != null)
                    {
                        context[entry.Key] = decoded;
                    }
                    // A null decode (e.g. a malformed ExpiryTimeUtc) DROPS the key: the core treats an absent key the
                    // same as null and its null-guard short-circuits, rather than stamping a bogus value or faulting.
                    continue;
                }

                // UNKNOWN keys: preserve verbatim. Force-decoding an unknown byte[] would corrupt a genuine binary
                // header, and the numeric delivery-count tolerance is owned by RabbitMqReceiver.ReadHeaderAsLong.
                context[entry.Key] = entry.Value;
            }

            return context;
        }

        /// <summary>
        /// The inbound header-value decode helper the translator delegates to for a native-frame field whose value
        /// is sourced from its decoded header copy when the native frame is absent (e.g. the dual-home CorrelationId
        /// delivered only as a header). A string-typed value arrives as a byte[] (longstr) from a real broker or as
        /// a string from an in-process double; decode the byte[] as UTF-8 and pass a string through unchanged.
        /// </summary>
        public static object DecodeHeaderValue(object value) => DecodeStringTypedValue(value);

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

        // ExpiryTimeUtc ENCODE (outbound): a DateTime the field table cannot encode is rendered to a round-trippable
        // ISO-8601 ("O") invariant string (it has no native AMQP property the way TimeToLive does). A non-DateTime
        // value falls back to the general outbound coercion so the publish never throws.
        private static object EncodeExpiryTimeUtc(object value)
            => value is DateTime expiry ? expiry.ToString("O", CultureInfo.InvariantCulture) : CoerceOutboundValue(value);

        // ExpiryTimeUtc DECODE (inbound): the wire value (a byte[] longstr from a real broker or a string from an
        // in-process double) is parsed back to a DateTime so the core's (DateTime?) cast in RefreshTimeToLive holds.
        // RoundtripKind preserves the "O" form's UTC/offset semantics. A malformed/unparseable value returns null so
        // ToContext DROPS the key (the core's null-guard short-circuits) rather than stamping a bogus DateTime or
        // faulting — NEVER throws.
        private static object DecodeExpiryTimeUtc(object value)
        {
            string text;
            switch (value)
            {
                case byte[] asBytes:
                    text = Encoding.UTF8.GetString(asBytes);
                    break;
                case string asString:
                    text = asString;
                    break;
                case DateTime asDateTime:
                    return asDateTime;
                default:
                    return null;
            }

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (object)null;
        }
    }
}
