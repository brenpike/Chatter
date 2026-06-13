using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Sending;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Chatter.MessageBrokers.RabbitMQ
{
    /// <summary>
    /// The SINGLE bidirectional translation contract between core <see cref="MessageContext"/> values and the
    /// AMQP wire representation (native <see cref="BasicProperties"/> frame fields plus the application-header
    /// field table). All three boundaries — SEND (<see cref="ToAmqp"/>), RECEIVE (<see cref="ToCore"/>), and
    /// REPUBLISH (<see cref="ToRepublishAmqp"/>) — route through this one type and the one declarative
    /// <see cref="_fieldMap"/> table below, so no boundary re-decides a field's native-vs-header home or its
    /// accessor independently. Each field's home, core binding, and CLR&lt;-&gt;wire coercion is declared once.
    /// </summary>
    /// <remarks>
    /// The field-map table holds the fields with a NATIVE frame home AND a core concept (MessageId, ContentType,
    /// CorrelationId, TimeToLive/Expiration). Each descriptor declares: the field's native get/set over
    /// <see cref="BasicProperties"/> (out) and <see cref="IReadOnlyBasicProperties"/> (in, guarded by Is*Present()),
    /// the core OUT accessor over an <see cref="OutboundBrokeredMessage"/>, and the core IN key it writes into the
    /// emitted context (null when the field has no core key sink, e.g. MessageId which the receiver carries on the
    /// MessageBrokerContext itself).
    ///
    /// HEADER-home fields are NOT enumerated as descriptors here: they are every remaining context entry, coerced
    /// table-legal outbound and rehydrated inbound through <see cref="RabbitMqHeaderMarshaller"/> (the header-coercion
    /// helper). There is no per-key allowlist in this translator — the marshaller's single per-descriptor
    /// symmetric-coercion table is the sole declaration of which header keys carry a known CLR coercion (each encoding
    /// CLR-&gt;wire outbound AND decoding wire-&gt;the SAME original CLR type inbound, so a non-string core key such as
    /// ExpiryTimeUtc rehydrates by construction), and this translator's descriptors own the native-home keys, so a
    /// header field cannot drift its home.
    ///
    /// DECISION-B (carry-only C-family — ContentEncoding / Type / AppId / Priority / Timestamp): these have a
    /// native frame home but NO core concept. They are captured inbound onto <see cref="ReceivedMessage"/> (the
    /// republish-carry facts) and re-applied on republish, but are NEVER surfaced into the core context and are
    /// NEVER sourced from a core accessor on send.
    /// DECISION-D (CorrelationId): DUAL-HOME — the native frame field AND a header copy, declared explicitly by the
    /// descriptor's <see cref="NativeFieldDescriptor.DualHomeHeader"/> flag.
    /// DECISION-E (persistence): <c>Persistent = true</c> is HARDCODED on send and republish; the delivered
    /// delivery-mode is never carried.
    /// </remarks>
    internal static class RabbitMqMessageTranslator
    {
        // A field with a native AMQP frame home and a core concept. Declares its native get/set over the outbound
        // BasicProperties / the captured NativeFacts, its core OUT source, and the core IN key sink (null = no core
        // key; the value is carried elsewhere, e.g. MessageId on the MessageBrokerContext). All values handled here
        // are string-shaped on the wire, so the descriptor carries no per-field coercion beyond the get/set lambdas.
        private sealed class NativeFieldDescriptor
        {
            public NativeFieldDescriptor(string name,
                                         Func<OutboundBrokeredMessage, string> coreOut,
                                         Action<BasicProperties, string> nativeSet,
                                         Func<NativeFacts, string> factsGet,
                                         string coreInKey,
                                         bool dualHomeHeader = false)
            {
                Name = name;
                CoreOut = coreOut;
                NativeSet = nativeSet;
                FactsGet = factsGet;
                CoreInKey = coreInKey;
                DualHomeHeader = dualHomeHeader;
            }

            public string Name { get; }
            // The core OUT accessor: reads the field's value off the outbound message (null/blank => skip).
            public Func<OutboundBrokeredMessage, string> CoreOut { get; }
            // Sets the native frame field on the outbound BasicProperties.
            public Action<BasicProperties, string> NativeSet { get; }
            // Reads the field's value off the captured NativeFacts inbound (null when the delivery carried none).
            public Func<NativeFacts, string> FactsGet { get; }
            // The core context key the inbound native value is written to (null = no core key sink).
            public string CoreInKey { get; }
            // DECISION-D: when true the field is ALSO written as a header copy outbound, in addition to the frame.
            public bool DualHomeHeader { get; }
        }

        // The one declarative field-map table. Native-home fields that carry a core concept. The C-family
        // carry-only natives are NOT here (no core concept — DECISION-B); they are handled by the facts capture.
        // TimeToLive/Expiration is handled by a dedicated arm in ToAmqp/ToCore (the only field needing
        // TimeSpan<->ms-string coercion and the TimeToLive-key drop), so it is not a string-shaped descriptor.
        private static readonly NativeFieldDescriptor[] _fieldMap =
        {
            // MessageId: native frame only, no core key sink (the receiver carries the id on the MessageBrokerContext).
            new NativeFieldDescriptor(
                name: "MessageId",
                coreOut: msg => msg.MessageId,
                nativeSet: (props, value) => props.MessageId = value,
                factsGet: facts => facts.MessageId,
                coreInKey: null),

            // ContentType: native frame field; inbound surfaces into MessageContext.ContentType so the receiver can
            // pick the body converter from the delivered content-type (GAP B). Outbound source is the
            // actual-serialization stamp the core wrote, with the sender's resolved-converter fallback applied by
            // the caller; its CoreOut is null and ToAmqp sets the frame directly.
            new NativeFieldDescriptor(
                name: "ContentType",
                coreOut: null,
                nativeSet: (props, value) => props.ContentType = value,
                factsGet: facts => facts.ContentType,
                coreInKey: MessageContext.ContentType),

            // CorrelationId: DUAL-HOME (DECISION-D) — native frame field AND a header copy. Inbound surfaces into
            // MessageContext.CorrelationId (the core casts it straight to string at InboundBrokeredMessage ctor).
            new NativeFieldDescriptor(
                name: "CorrelationId",
                coreOut: msg => msg.CorrelationId,
                nativeSet: (props, value) => props.CorrelationId = value,
                factsGet: facts => facts.CorrelationId,
                coreInKey: MessageContext.CorrelationId,
                dualHomeHeader: true)
        };

        /// <summary>
        /// The curated native AMQP properties a delivery carried that have NO core concept (DECISION-B C-family),
        /// captured inbound so the republish hops can re-apply them. Surfaced from <see cref="ToCore"/> alongside
        /// the core context; never written into the core context.
        /// </summary>
        internal readonly struct NativeFacts
        {
            public NativeFacts(string messageId,
                               string expiration,
                               byte? priority,
                               AmqpTimestamp? timestamp,
                               string type,
                               string appId,
                               string contentEncoding,
                               string contentType,
                               string correlationId)
            {
                MessageId = messageId;
                Expiration = expiration;
                Priority = priority;
                Timestamp = timestamp;
                Type = type;
                AppId = appId;
                ContentEncoding = contentEncoding;
                ContentType = contentType;
                CorrelationId = correlationId;
            }

            public string MessageId { get; }
            public string Expiration { get; }
            public byte? Priority { get; }
            public AmqpTimestamp? Timestamp { get; }
            public string Type { get; }
            public string AppId { get; }
            public string ContentEncoding { get; }
            public string ContentType { get; }
            public string CorrelationId { get; }
        }

        /// <summary>
        /// Per-republish-hop knobs for <see cref="ToRepublishAmqp"/>: <see cref="PreserveExpiration"/> is true on
        /// the nack-redelivery hop (keep the per-message TTL) and false on the deadletter hop (a DLQ message must
        /// not auto-expire via the original TTL); <see cref="HeaderOverrides"/> are merged over the carried headers
        /// (e.g. the incremented classic delivery-count or the failure-detail strings).
        /// </summary>
        internal readonly struct RepublishOptions
        {
            public RepublishOptions(bool preserveExpiration, IReadOnlyDictionary<string, object> headerOverrides)
            {
                PreserveExpiration = preserveExpiration;
                HeaderOverrides = headerOverrides;
            }

            public bool PreserveExpiration { get; }
            public IReadOnlyDictionary<string, object> HeaderOverrides { get; }
        }

        /// <summary>
        /// SEND boundary: translate an outbound core message into the AMQP wire representation — the native
        /// <see cref="BasicProperties"/> frame and the application-header field table. Walks the field-map table
        /// once (native-home descriptors set the frame from their core accessor; the dual-home CorrelationId also
        /// writes a header copy), lifts TimeToLive onto the native Expiration (dropping the un-encodable
        /// TimeToLive key), and routes the remaining context through the marshaller. <c>Persistent = true</c> is
        /// hardcoded (DECISION-E). C-family natives are NOT sourced (DECISION-B). ContentType is sourced from the
        /// actual-serialization stamp with the supplied converter fallback.
        /// </summary>
        public static BasicProperties ToAmqp(OutboundBrokeredMessage message, string fallbackContentType)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var properties = new BasicProperties
            {
                // DECISION-E: durable delivery hardcoded so a message survives a broker restart on a durable queue.
                Persistent = true
            };

            // The header table starts from the full outbound context, coerced table-legal by the marshaller (the
            // sole header-coercion boundary). The marshaller drops the un-encodable TimeToLive key.
            var headerTable = RabbitMqHeaderMarshaller.ToHeaderTable(message.MessageContext, properties);

            // ContentType: actual-serialization stamp wins; fall back to the caller-supplied resolved converter.
            var stampedContentType = message.GetMessageContextByKey<string>(MessageContext.ContentType);
            properties.ContentType = string.IsNullOrWhiteSpace(stampedContentType) ? fallbackContentType : stampedContentType;

            // Walk the native-home descriptors that carry a core concept. ContentType is sourced above (its CoreOut
            // is null), so skip the null-CoreOut descriptors here.
            foreach (var descriptor in _fieldMap)
            {
                if (descriptor.CoreOut is null)
                {
                    continue;
                }

                var value = descriptor.CoreOut(message);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                descriptor.NativeSet(properties, value);
                if (descriptor.DualHomeHeader)
                {
                    // DECISION-D: keep the dual-home header copy. The marshaller already copied the context entry
                    // into the table, so the copy is present; assert it explicitly so a context that lacked the key
                    // still carries the header form on the wire.
                    headerTable[descriptor.CoreInKey] = value;
                }
            }

            // TTL -> native Expiration (ms string), tolerant of a live TimeSpan AND an outbox-replayed string form.
            // The marshaller already dropped the TimeToLive header key.
            var expiration = ResolveExpiration(message.GetTimeToLive());
            if (expiration != null)
            {
                properties.Expiration = expiration;
            }

            properties.Headers = headerTable;
            return properties;
        }

        /// <summary>
        /// RECEIVE boundary: translate the captured <see cref="NativeFacts"/> + delivered header table into the
        /// core-ready context plus the delivered content-type. Walks the field-map table once (native-home
        /// descriptors read off the facts and write to their bound core key when one exists — closing GAP B by
        /// surfacing the delivered ContentType), decodes the header table through the marshaller (closing GAP F —
        /// table/descriptor-driven decode, no per-key allowlist branch here), and reconstitutes the native
        /// Expiration into MessageContext.TimeToLive (GAP A). The C-family natives stay ONLY on the facts
        /// (DECISION-B), never in the core context. The returned <paramref name="deliveredContentType"/> lets the
        /// receiver pick the inbound body converter from the delivered content-type (GAP B).
        /// </summary>
        public static (IDictionary<string, object> coreContext, string deliveredContentType) ToCore(
            NativeFacts facts,
            IReadOnlyDictionary<string, object> deliveredHeaders)
        {
            // Header-home fields: decode the delivered header table through the marshaller (the sole boundary).
            var coreContext = new Dictionary<string, object>(RabbitMqHeaderMarshaller.ToContext(deliveredHeaders));

            string deliveredContentType = null;

            // Native-home descriptors with a core concept: the native frame value (carried on the facts) is
            // authoritative. When the frame is absent (e.g. the dual-home CorrelationId delivered ONLY as a header —
            // DECISION-D), fall back to the header copy already in coreContext, decoded byte[]->string via the
            // marshaller helper so the core's unguarded (string) cast (InboundBrokeredMessage casts CorrelationId at
            // ctor) holds either way.
            foreach (var descriptor in _fieldMap)
            {
                if (descriptor.CoreInKey is null)
                {
                    continue;
                }

                var value = descriptor.FactsGet(facts);
                if (value is null && coreContext.TryGetValue(descriptor.CoreInKey, out var headerCopy))
                {
                    value = RabbitMqHeaderMarshaller.DecodeHeaderValue(headerCopy) as string;
                }

                if (value is null)
                {
                    continue;
                }

                coreContext[descriptor.CoreInKey] = value;
                if (descriptor.CoreInKey == MessageContext.ContentType)
                {
                    deliveredContentType = value;
                }
            }

            // GAP A: reconstitute the native Expiration (ms string) into MessageContext.TimeToLive as a TimeSpan so
            // the core concept survives the receive hop (a downstream RefreshTimeToLive / re-send can read it).
            var ttl = ReconstituteTimeToLive(facts.Expiration);
            if (ttl != null)
            {
                coreContext[MessageContext.TimeToLive] = ttl.Value;
            }

            return (coreContext, deliveredContentType);
        }

        /// <summary>
        /// REPUBLISH boundary: rebuild the outbound AMQP representation for a redelivery / deadletter hop from the
        /// carried <see cref="NativeFacts"/> and headers, routing through the SAME native-frame + header-table
        /// construction the send path uses. <c>Persistent = true</c> hardcoded (DECISION-E). Every carried native
        /// (including the C-family — DECISION-B) is re-applied; Expiration only when
        /// <see cref="RepublishOptions.PreserveExpiration"/>. The merged header bag (carried + overrides) is routed
        /// through the marshaller so the republish is field-table-legal exactly like a fresh publish.
        /// </summary>
        public static BasicProperties ToRepublishAmqp(NativeFacts facts,
                                                      IReadOnlyDictionary<string, object> carriedHeaders,
                                                      RepublishOptions options)
        {
            var properties = new BasicProperties
            {
                Persistent = true
            };

            properties.Headers = RabbitMqHeaderMarshaller.ToHeaderTable(
                MergeHeaders(carriedHeaders, options.HeaderOverrides), properties);

            if (!string.IsNullOrEmpty(facts.MessageId))
            {
                properties.MessageId = facts.MessageId;
            }

            // Re-apply each carried native AMQP property when the delivery carried one (absent => null => skip, so a
            // republish never stamps a spurious default). Expiration ONLY when preserveExpiration.
            if (options.PreserveExpiration && facts.Expiration != null)
            {
                properties.Expiration = facts.Expiration;
            }
            if (facts.Priority.HasValue)
            {
                properties.Priority = facts.Priority.Value;
            }
            if (facts.Timestamp.HasValue)
            {
                properties.Timestamp = facts.Timestamp.Value;
            }
            if (facts.Type != null)
            {
                properties.Type = facts.Type;
            }
            if (facts.AppId != null)
            {
                properties.AppId = facts.AppId;
            }
            if (facts.ContentEncoding != null)
            {
                properties.ContentEncoding = facts.ContentEncoding;
            }
            if (facts.ContentType != null)
            {
                properties.ContentType = facts.ContentType;
            }
            if (facts.CorrelationId != null)
            {
                properties.CorrelationId = facts.CorrelationId;
            }

            return properties;
        }

        /// <summary>
        /// The SINGLE capture point for the curated native AMQP property set, read ONCE from the delivery's
        /// <see cref="IReadOnlyBasicProperties"/> using its Is*Present() guards (absent => null, never a spurious
        /// default). The receiver calls this at buffer time and carries the result for both the receive translation
        /// (<see cref="ToCore"/>) and the republish translation (<see cref="ToRepublishAmqp"/>). The C-family
        /// natives (DECISION-B) live ONLY on the returned facts, for republish carry, never in the core context.
        /// </summary>
        public static NativeFacts CaptureFacts(IReadOnlyBasicProperties properties)
        {
            if (properties is null)
            {
                return default;
            }

            return new NativeFacts(
                messageId: properties.IsMessageIdPresent() ? properties.MessageId : null,
                expiration: properties.IsExpirationPresent() ? properties.Expiration : null,
                priority: properties.IsPriorityPresent() ? properties.Priority : (byte?)null,
                timestamp: properties.IsTimestampPresent() ? properties.Timestamp : (AmqpTimestamp?)null,
                type: properties.IsTypePresent() ? properties.Type : null,
                appId: properties.IsAppIdPresent() ? properties.AppId : null,
                contentEncoding: properties.IsContentEncodingPresent() ? properties.ContentEncoding : null,
                contentType: properties.IsContentTypePresent() ? properties.ContentType : null,
                correlationId: properties.IsCorrelationIdPresent() ? properties.CorrelationId : null);
        }

        // Lift TimeToLive onto the native Expiration (ms string). A non-positive TTL floors at "0"; fractional
        // milliseconds are floored by the (long) truncation. Null TimeToLive leaves Expiration unset.
        private static string ResolveExpiration(TimeSpan? timeToLive)
        {
            if (timeToLive == null)
            {
                return null;
            }

            var milliseconds = timeToLive.Value.TotalMilliseconds <= 0d ? 0L : (long)timeToLive.Value.TotalMilliseconds;
            return milliseconds.ToString(CultureInfo.InvariantCulture);
        }

        // GAP A: reconstitute the native Expiration (ms string) back into a TimeSpan TimeToLive. A malformed or
        // negative value is ignored (returns null) rather than stamping a bogus TimeToLive.
        private static TimeSpan? ReconstituteTimeToLive(string expiration)
        {
            if (expiration is null)
            {
                return null;
            }

            if (!long.TryParse(expiration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
                || milliseconds < 0L)
            {
                return null;
            }

            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private static Dictionary<string, object> MergeHeaders(IReadOnlyDictionary<string, object> source,
                                                               IReadOnlyDictionary<string, object> overrides)
        {
            var merged = new Dictionary<string, object>();
            if (source != null)
            {
                foreach (var entry in source)
                {
                    merged[entry.Key] = entry.Value;
                }
            }

            if (overrides != null)
            {
                foreach (var entry in overrides)
                {
                    merged[entry.Key] = entry.Value;
                }
            }

            return merged;
        }
    }
}
