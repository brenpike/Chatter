using Chatter.MessageBrokers;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Chatter.MessageBrokers.RabbitMQ
{
    /// <summary>
    /// The header-coercion HELPER that <see cref="RabbitMqMessageTranslator"/>'s header-home fields delegate to.
    /// Coerces each outbound <see cref="MessageContext"/> CLR value to an AMQP-field-table-legal wire type
    /// (<see cref="ToHeaderTable"/>) and, inbound (<see cref="ToContext"/>), applies an EXPLICIT per-core-key
    /// disposition to every received header named after a core <see cref="MessageContext"/> key so an uncoerced or
    /// untrusted value can never cross under a core key.
    /// The translator owns the native-frame fields and routes every header bag through this helper; this type is
    /// the header arm of the single translation contract, not a standalone boundary.
    /// </summary>
    /// <remarks>
    /// INBOUND disposition layer (closed-by-construction). The authoritative registry of core context keys is the set
    /// of <c>public static readonly string</c> fields on <see cref="MessageContext"/>; this helper DERIVES that set by
    /// reflection at type init (it reads the core registry as ground truth and never restates it). Every reflected core
    /// key MUST carry an explicit <see cref="HeaderDisposition"/> in <c>_dispositions</c>; the type initializer ASSERTS
    /// completeness and THROWS naming any reflected core key that lacks a disposition. The invariant is therefore
    /// closed-by-construction: NO header named after a core key is ever preserved verbatim into the core context, and a
    /// NEW core key added to <see cref="MessageContext"/> cannot ship without an explicit disposition decision — it
    /// fails this type's static init (surfacing as <see cref="TypeInitializationException"/>) until dispositioned.
    /// The three dispositions: <see cref="HeaderDisposition.DecodeString"/> rehydrates a byte[]/string AMQP longstr to
    /// a <c>string</c> (the inbound receive path casts these keys straight to <c>string</c>); this includes
    /// CorrelationId and ContentType — string-typed DECISION-D dual-home keys whose decoded header copy must survive in
    /// the core context as a fallback for the translator's native-frame assignment (the translator re-sources them from
    /// the native frame when present and overwrites; when the native frame is absent the decoded copy is the
    /// authoritative value — <c>Drop</c> removed that copy and broke the fallback);
    /// <see cref="HeaderDisposition.DecodeDateTime"/> parses the wire value back to a <c>DateTime</c> (so the core's
    /// <c>(DateTime?)</c> cast in <c>OutboundBrokeredMessage.RefreshTimeToLive</c> on
    /// <see cref="MessageContext.ExpiryTimeUtc"/> holds after a round trip), null-dropping on a malformed value;
    /// <see cref="HeaderDisposition.Drop"/> omits the key from the core context entirely because its authoritative value
    /// comes from elsewhere (TimeToLive is lifted onto native Expiration; ReceiveAttempts is the numeric delivery-count
    /// path owned by <c>RabbitMqReceiver.ReadHeaderAsLong</c>; IsError is receiver-derived;
    /// <see cref="MessageContext.ChatterBaseHeader"/> is a namespace prefix, not a real header) so a foreign copy of
    /// it is untrusted. A NON-core key (genuine custom or binary header) is preserved VERBATIM — the flexible-storage
    /// premise — so real binary headers are never corrupted.
    /// </remarks>
    /// <remarks>
    /// OUTBOUND (<see cref="ToHeaderTable"/>): the AMQP 0-9-1 field table the client can encode admits only
    /// <c>string</c>, <c>bool</c>, <c>sbyte</c>, <c>int</c>, <c>long</c>, <c>decimal</c>, <c>byte[]</c>, and a
    /// nested <c>IDictionary</c> — NOT <see cref="TimeSpan"/>, <see cref="DateTime"/>, <see cref="Guid"/>,
    /// <c>ulong</c>, <c>byte</c>, <c>uint</c>, or <c>ushort</c>. The core stamps several of those CLR types
    /// onto the context (most notably <see cref="MessageContext.TimeToLive"/> as a <see cref="TimeSpan"/> via
    /// <c>OutboundBrokeredMessage.WithTimeToLive</c>), so a raw copy of the context into the header table threw
    /// at publish. This helper coerces each value to a table-legal form and never throws and never silently drops
    /// a populated value. <see cref="MessageContext.ExpiryTimeUtc"/> (a <see cref="DateTime"/> with no native AMQP
    /// property of its own) is ISO-8601 ("O") encoded so it round-trips back to a <see cref="DateTime"/> via its
    /// inbound <see cref="HeaderDisposition.DecodeDateTime"/> disposition. TTL lifting is NOT owned here: the translator
    /// lifts <see cref="MessageContext.TimeToLive"/> onto the native <see cref="BasicProperties.Expiration"/> from the
    /// core's authoritative <c>OutboundBrokeredMessage.GetTimeToLive()</c>; this helper only DROPS the un-encodable
    /// <see cref="MessageContext.TimeToLive"/> key so it never reaches the table in any form.
    /// </remarks>
    internal static class RabbitMqHeaderMarshaller
    {
        // The inbound disposition for one core header key: how a received header named after that core key is projected
        // into the core context. INVARIANT: every reflected core key carries exactly one of these (completeness is
        // asserted at type init), so no core-key-named header is ever preserved verbatim.
        private enum HeaderDisposition
        {
            // Rehydrate the wire value (byte[] AMQP longstr from a real broker, or string from an in-process double)
            // to a string; the core casts these keys straight to (string).
            DecodeString,
            // Parse the wire value back to a DateTime; null-drop on a malformed/unparseable value so the core's
            // null-guard short-circuits cleanly (NEVER stamp a bogus DateTime, NEVER throw).
            DecodeDateTime,
            // Omit the key from the core context entirely: its authoritative value comes from elsewhere (native frame,
            // native Expiration lift, the numeric delivery-count path) or a foreign copy of it is untrusted.
            Drop
        }

        // INVARIANT: an EXPLICIT disposition for EVERY core MessageContext key. The reflected core-key set (see the
        // static initializer) is asserted to be a SUBSET of these entries' keys at type init; a reflected core key
        // missing here throws naming the offender, so a new core key cannot ship without an explicit disposition.
        // DecodeString (12): the string-typed routing/failure keys the inbound receive path casts straight to (string)
        //   and a real broker surfaces as a byte[] longstr — plus CorrelationId and ContentType, which are
        //   string-typed DECISION-D dual-home keys: the translator re-sources them from the native frame when present
        //   (overwrites), and falls back to the decoded header copy when the native frame is absent. Drop would remove
        //   that fallback copy; DecodeString preserves it safely (byte[]→string is exactly the (string) cast the core
        //   uses) and eliminates raw byte[] under a core key.
        // DecodeDateTime (1): ExpiryTimeUtc, a non-string (DateTime) core key.
        // Drop (4): TimeToLive (lifted onto native Expiration), ReceiveAttempts (numeric delivery-count path owned by
        //   RabbitMqReceiver), IsError (receiver-derived), ChatterBaseHeader (the "Chatter" namespace prefix, not a
        //   real header).
        private static readonly IReadOnlyDictionary<string, HeaderDisposition> _dispositions =
            new Dictionary<string, HeaderDisposition>
            {
                [MessageContext.Via] = HeaderDisposition.DecodeString,
                [MessageContext.Subject] = HeaderDisposition.DecodeString,
                [MessageContext.GroupId] = HeaderDisposition.DecodeString,
                [MessageContext.RouteToSelfPath] = HeaderDisposition.DecodeString,
                [MessageContext.ReplyToAddress] = HeaderDisposition.DecodeString,
                [MessageContext.ReplyToGroupId] = HeaderDisposition.DecodeString,
                [MessageContext.RoutingSlip] = HeaderDisposition.DecodeString,
                [MessageContext.FailureDetails] = HeaderDisposition.DecodeString,
                [MessageContext.FailureDescription] = HeaderDisposition.DecodeString,
                [MessageContext.InfrastructureType] = HeaderDisposition.DecodeString,

                [MessageContext.ExpiryTimeUtc] = HeaderDisposition.DecodeDateTime,

                [MessageContext.CorrelationId] = HeaderDisposition.DecodeString,
                [MessageContext.ContentType] = HeaderDisposition.DecodeString,

                [MessageContext.TimeToLive] = HeaderDisposition.Drop,
                [MessageContext.ReceiveAttempts] = HeaderDisposition.Drop,
                [MessageContext.IsError] = HeaderDisposition.Drop,
                [MessageContext.ChatterBaseHeader] = HeaderDisposition.Drop
            };

        // COMPLETENESS ASSERTION (closed-by-construction acceptance gate). At type init, derive the authoritative core-
        // key set by reflecting MessageContext's public static readonly string fields (the core registry is ground
        // truth; the adapter reads it, never restates it) and assert EVERY reflected core key has an explicit
        // disposition above. A missing disposition THROWS naming the offender(s), surfacing as a
        // TypeInitializationException under the first test/use touching this type — so a NEW MessageContext key forces
        // an explicit disposition decision or the adapter fails immediately. The private composing fields (Routing,
        // Infrastructure) are excluded by the Public|Static filter; ChatterBaseHeader IS a public static readonly
        // string and so is in the reflected set — its explicit Drop disposition keeps completeness satisfied.
        static RabbitMqHeaderMarshaller()
        {
            var coreKeys = typeof(MessageContext)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.FieldType == typeof(string))
                .Select(field => (string)field.GetValue(null))
                .ToList();

            var undispositioned = coreKeys
                .Where(key => !_dispositions.ContainsKey(key))
                .ToList();

            if (undispositioned.Count > 0)
            {
                throw new InvalidOperationException(
                    "RabbitMqHeaderMarshaller is missing an inbound HeaderDisposition for the following core " +
                    "MessageContext key(s); every core key must declare an explicit disposition: " +
                    string.Join(", ", undispositioned) + ".");
            }
        }

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

                // ExpiryTimeUtc (a DateTime) is ISO-8601 ("O") encoded so it round-trips to a DateTime via its inbound
                // DecodeDateTime disposition; every other key falls through to the general outbound coercion.
                table[entry.Key] = entry.Key == MessageContext.ExpiryTimeUtc
                    ? EncodeExpiryTimeUtc(entry.Value)
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
        /// Projects a received AMQP header bag into a core-ready context dictionary by applying each core key's
        /// EXPLICIT inbound disposition: a <see cref="HeaderDisposition.Drop"/> key is omitted; a
        /// <see cref="HeaderDisposition.DecodeString"/> key is rehydrated to <c>string</c> (a byte[] AMQP longstr
        /// decodes to UTF-8); a <see cref="HeaderDisposition.DecodeDateTime"/> key is parsed back to a
        /// <c>DateTime</c>; a decode that returns null DROPS the key so the core's null-guard short-circuits cleanly.
        /// A NON-core key is preserved verbatim so genuine binary headers are untouched. No header named after a core
        /// key is ever preserved verbatim — the disposition set is complete by construction (asserted at type init).
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
                if (_dispositions.TryGetValue(entry.Key, out var disposition))
                {
                    switch (disposition)
                    {
                        case HeaderDisposition.Drop:
                            // Omit: the authoritative value comes from elsewhere (native frame, native Expiration lift,
                            // numeric delivery-count path) or a foreign copy of this core key is untrusted.
                            continue;
                        case HeaderDisposition.DecodeString:
                        {
                            var decodedString = DecodeStringTypedValue(entry.Value);
                            if (decodedString != null)
                            {
                                context[entry.Key] = decodedString;
                            }
                            // A null decode DROPS the key: the core treats an absent key the same as null.
                            continue;
                        }
                        case HeaderDisposition.DecodeDateTime:
                        {
                            var decodedDateTime = DecodeExpiryTimeUtc(entry.Value);
                            if (decodedDateTime != null)
                            {
                                context[entry.Key] = decodedDateTime;
                            }
                            // A null decode (e.g. a malformed ExpiryTimeUtc) DROPS the key: the core's null-guard
                            // short-circuits rather than stamping a bogus value or faulting.
                            continue;
                        }
                    }
                }

                // NON-core keys: preserve verbatim. Force-decoding an unknown byte[] would corrupt a genuine binary
                // header (the flexible-storage premise for custom headers).
                context[entry.Key] = entry.Value;
            }

            return context;
        }

        /// <summary>
        /// The inbound header-value decode helper the translator delegates to for a native-frame field whose value
        /// is sourced from its decoded header copy when the native frame is absent (e.g. the dual-home CorrelationId
        /// delivered only as a header). A string-typed value arrives as a byte[] (longstr) from a real broker or as
        /// a string from an in-process double; decode the byte[] as UTF-8 and pass a string through unchanged. This
        /// decodes the RAW delivered header value independently of <see cref="ToContext"/> output.
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
