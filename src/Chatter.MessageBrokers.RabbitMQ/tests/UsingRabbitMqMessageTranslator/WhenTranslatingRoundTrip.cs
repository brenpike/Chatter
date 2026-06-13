using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.UsingRabbitMqMessageTranslator
{
    // Pins the SINGLE bidirectional translation contract directly: every descriptor's per-field round-trip
    // symmetry across the SEND (ToAmqp), RECEIVE (ToCore), and REPUBLISH (ToRepublishAmqp) boundaries, plus the
    // gap closures (A: TTL reconstituted into core; B: delivered content-type surfaced) and the locked decisions
    // (B: C-family carry-only natives never surfaced into core but re-applied on republish; D: correlation-id
    // dual-home; E: Persistent hardcoded). No live broker — the translator is exercised over BasicProperties /
    // IReadOnlyBasicProperties directly.
    public class WhenTranslatingRoundTrip : Testing.Core.Context
    {
        private const string Destination = "target-queue";

        private static OutboundBrokeredMessage Message(IDictionary<string, object> messageContext = null)
            => new OutboundBrokeredMessage(
                Guid.NewGuid().ToString(),
                new byte[] { 1, 2, 3 },
                messageContext ?? new Dictionary<string, object>(),
                Destination,
                new RabbitMqBodyConverter());

        // Builds an IReadOnlyBasicProperties modelling a delivery: native frame fields set only when supplied so
        // the translator's Is*Present()-guarded capture sees absent fields as absent.
        private static BasicProperties Delivered(
            string messageId = null,
            string contentType = null,
            string correlationId = null,
            string expiration = null,
            byte? priority = null,
            AmqpTimestamp? timestamp = null,
            string type = null,
            string appId = null,
            string contentEncoding = null,
            IDictionary<string, object> headers = null)
        {
            var properties = new BasicProperties { Headers = headers };
            if (messageId != null) properties.MessageId = messageId;
            if (contentType != null) properties.ContentType = contentType;
            if (correlationId != null) properties.CorrelationId = correlationId;
            if (expiration != null) properties.Expiration = expiration;
            if (priority.HasValue) properties.Priority = priority.Value;
            if (timestamp.HasValue) properties.Timestamp = timestamp.Value;
            if (type != null) properties.Type = type;
            if (appId != null) properties.AppId = appId;
            if (contentEncoding != null) properties.ContentEncoding = contentEncoding;
            return properties;
        }

        // --- DECISION-E: Persistent hardcoded on send and republish ---

        [Fact]
        public void MustSetPersistentTrueOnSend()
        {
            var properties = RabbitMqMessageTranslator.ToAmqp(Message(), "application/json");
            properties.Persistent.Should().BeTrue();
        }

        [Fact]
        public void MustSetPersistentTrueOnRepublish()
        {
            var properties = RabbitMqMessageTranslator.ToRepublishAmqp(
                default,
                new Dictionary<string, object>(),
                new RabbitMqMessageTranslator.RepublishOptions(preserveExpiration: true, headerOverrides: null));
            properties.Persistent.Should().BeTrue();
        }

        // --- MessageId descriptor: native frame on send, carried on facts, no core key ---

        [Fact]
        public void MustSetMessageIdOnNativeFrameForSend()
        {
            var message = Message();
            var properties = RabbitMqMessageTranslator.ToAmqp(message, "application/json");
            properties.MessageId.Should().Be(message.MessageId);
        }

        // --- ContentType descriptor: stamp wins on send; GAP B surfaces it on receive ---

        [Fact]
        public void MustAdvertiseStampedContentTypeOverFallbackOnSend()
        {
            var message = Message();
            message.MessageContext[MessageContext.ContentType] = "application/x-custom";

            var properties = RabbitMqMessageTranslator.ToAmqp(message, "application/json");

            properties.ContentType.Should().Be("application/x-custom");
        }

        [Fact]
        public void MustFallBackToFallbackContentTypeWhenStampAbsentOnSend()
        {
            var message = Message();
            message.MessageContext.Remove(MessageContext.ContentType);

            var properties = RabbitMqMessageTranslator.ToAmqp(message, "application/x-fallback");

            properties.ContentType.Should().Be("application/x-fallback");
        }

        // GAP B: the delivered native ContentType is surfaced into the core context AND returned so the receiver
        // can select the inbound body converter from it.
        [Fact]
        public void MustSurfaceDeliveredContentTypeOnReceive()
        {
            var (coreContext, deliveredContentType) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(contentType: "application/x-delivered")),
                new Dictionary<string, object>());

            deliveredContentType.Should().Be("application/x-delivered");
            coreContext[MessageContext.ContentType].Should().Be("application/x-delivered");
        }

        [Fact]
        public void MustReturnNullDeliveredContentTypeWhenFrameAbsentOnReceive()
        {
            var (_, deliveredContentType) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered()),
                new Dictionary<string, object>());

            deliveredContentType.Should().BeNull();
        }

        // --- DECISION-D: CorrelationId dual-home (native frame + header copy) on send; frame authoritative inbound ---

        [Fact]
        public void MustSetCorrelationIdOnBothNativeFrameAndHeaderForSend()
        {
            var message = Message().WithCorrelationId("corr-123");

            var properties = RabbitMqMessageTranslator.ToAmqp(message, "application/json");

            properties.CorrelationId.Should().Be("corr-123", "the native frame carries the correlation id");
            properties.Headers[MessageContext.CorrelationId].Should().Be("corr-123", "the dual-home header copy is also written");
        }

        [Fact]
        public void MustSurfaceCorrelationIdFromNativeFrameOnReceive()
        {
            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(correlationId: "corr-frame")),
                new Dictionary<string, object>());

            coreContext[MessageContext.CorrelationId].Should().Be("corr-frame");
        }

        // DECISION-D fallback: a delivery carrying CorrelationId ONLY as a longstr header (no native frame) still
        // surfaces a CLR string into the core key (decoded via the marshaller helper), so the core's unguarded
        // (string) cast holds.
        [Fact]
        public void MustDecodeCorrelationIdFromHeaderWhenFrameAbsentOnReceive()
        {
            var headers = new Dictionary<string, object>
            {
                [MessageContext.CorrelationId] = System.Text.Encoding.UTF8.GetBytes("corr-header")
            };

            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(headers: headers)),
                headers);

            coreContext[MessageContext.CorrelationId].Should().Be("corr-header");
        }

        // --- GAP A: TTL <-> native Expiration round-trip ---

        [Fact]
        public void MustLiftTimeToLiveOntoNativeExpirationAndDropKeyOnSend()
        {
            var message = Message();
            message.WithTimeToLive(TimeSpan.FromMilliseconds(1500));

            var properties = RabbitMqMessageTranslator.ToAmqp(message, "application/json");

            properties.Expiration.Should().Be("1500");
            properties.Headers.Should().NotContainKey(MessageContext.TimeToLive);
        }

        [Fact]
        public void MustFloorNonPositiveTimeToLiveAtZeroOnSend()
        {
            var message = Message();
            message.WithTimeToLive(TimeSpan.Zero);

            var properties = RabbitMqMessageTranslator.ToAmqp(message, "application/json");

            properties.Expiration.Should().Be("0");
        }

        // GAP A: the native Expiration (ms string) is reconstituted into MessageContext.TimeToLive as a TimeSpan so
        // the core concept survives the receive hop.
        [Fact]
        public void MustReconstituteNativeExpirationIntoTimeToLiveOnReceive()
        {
            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(expiration: "1500")),
                new Dictionary<string, object>());

            coreContext[MessageContext.TimeToLive].Should().Be(TimeSpan.FromMilliseconds(1500));
        }

        // GAP A round-trip: a TimeToLive sent and then received reconstitutes to the same TimeSpan.
        [Fact]
        public void MustRoundTripTimeToLiveSendThenReceive()
        {
            var message = Message();
            message.WithTimeToLive(TimeSpan.FromMilliseconds(2500));
            var sent = RabbitMqMessageTranslator.ToAmqp(message, "application/json");

            var sentHeaders = new Dictionary<string, object>(sent.Headers);
            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(sent),
                sentHeaders);

            coreContext[MessageContext.TimeToLive].Should().Be(TimeSpan.FromMilliseconds(2500));
        }

        [Fact]
        public void MustNotReconstituteTimeToLiveWhenExpirationAbsentOnReceive()
        {
            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered()),
                new Dictionary<string, object>());

            coreContext.Should().NotContainKey(MessageContext.TimeToLive);
        }

        // A malformed native Expiration is ignored rather than stamping a bogus TimeToLive.
        [Fact]
        public void MustIgnoreMalformedNativeExpirationOnReceive()
        {
            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(expiration: "not-a-number")),
                new Dictionary<string, object>());

            coreContext.Should().NotContainKey(MessageContext.TimeToLive);
        }

        // An Expiration that parses as a long but exceeds TimeSpan.MaxValue in milliseconds must be treated like a
        // malformed value (dropped) rather than throwing OverflowException — otherwise the delivery, already removed
        // from the local buffer, would have no ack/nack/deadletter path and (with prefetch 1) would stall the receiver.
        [Fact]
        public void MustIgnoreOutOfRangeNativeExpirationOnReceive()
        {
            var act = () =>
            {
                var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                    RabbitMqMessageTranslator.CaptureFacts(
                        Delivered(expiration: long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    new Dictionary<string, object>());

                coreContext.Should().NotContainKey(MessageContext.TimeToLive);
            };

            act.Should().NotThrow();
        }

        // --- DECISION-B: C-family carry-only natives never surfaced into core, re-applied on republish ---

        [Fact]
        public void MustNotSurfaceCFamilyNativesIntoCoreContextOnReceive()
        {
            var timestamp = new AmqpTimestamp(1718000000L);
            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(
                    priority: 4, timestamp: timestamp, type: "OrderPlaced", appId: "orders-svc", contentEncoding: "gzip")),
                new Dictionary<string, object>());

            // None of the carry-only C-family natives gain an app-facing core key.
            coreContext.Should().NotContainKey("Priority");
            coreContext.Should().NotContainKey("Timestamp");
            coreContext.Should().NotContainKey("Type");
            coreContext.Should().NotContainKey("AppId");
            coreContext.Should().NotContainKey("ContentEncoding");
        }

        // The C-family natives ARE carried on the facts and re-applied to the native frame on a republish hop.
        [Fact]
        public void MustReapplyCFamilyNativesOnRepublish()
        {
            var timestamp = new AmqpTimestamp(1718000000L);
            var facts = RabbitMqMessageTranslator.CaptureFacts(Delivered(
                priority: 4, timestamp: timestamp, type: "OrderPlaced", appId: "orders-svc", contentEncoding: "gzip"));

            var properties = RabbitMqMessageTranslator.ToRepublishAmqp(
                facts,
                new Dictionary<string, object>(),
                new RabbitMqMessageTranslator.RepublishOptions(preserveExpiration: true, headerOverrides: null));

            properties.Priority.Should().Be((byte)4);
            properties.Timestamp.Should().Be(timestamp);
            properties.Type.Should().Be("OrderPlaced");
            properties.AppId.Should().Be("orders-svc");
            properties.ContentEncoding.Should().Be("gzip");
        }

        // --- republish Expiration per-hop behavior ---

        [Fact]
        public void MustPreserveNativeExpirationOnRepublishWhenPreserveExpirationTrue()
        {
            var facts = RabbitMqMessageTranslator.CaptureFacts(Delivered(expiration: "60000"));

            var properties = RabbitMqMessageTranslator.ToRepublishAmqp(
                facts,
                new Dictionary<string, object>(),
                new RabbitMqMessageTranslator.RepublishOptions(preserveExpiration: true, headerOverrides: null));

            properties.Expiration.Should().Be("60000");
        }

        [Fact]
        public void MustDropNativeExpirationOnRepublishWhenPreserveExpirationFalse()
        {
            var facts = RabbitMqMessageTranslator.CaptureFacts(Delivered(expiration: "60000"));

            var properties = RabbitMqMessageTranslator.ToRepublishAmqp(
                facts,
                new Dictionary<string, object>(),
                new RabbitMqMessageTranslator.RepublishOptions(preserveExpiration: false, headerOverrides: null));

            properties.Expiration.Should().BeNull();
        }

        // Republish header overrides are merged over the carried headers and coerced table-legal by the marshaller.
        [Fact]
        public void MustMergeHeaderOverridesOnRepublish()
        {
            var carried = new Dictionary<string, object> { ["carried-key"] = "carried-value" };
            var overrides = new Dictionary<string, object> { [MessageContext.FailureDetails] = "poisoned" };

            var properties = RabbitMqMessageTranslator.ToRepublishAmqp(
                default,
                carried,
                new RabbitMqMessageTranslator.RepublishOptions(preserveExpiration: false, headerOverrides: overrides));

            properties.Headers["carried-key"].Should().Be("carried-value");
            properties.Headers[MessageContext.FailureDetails].Should().Be("poisoned");
        }

        // A republish re-applies the carried native CorrelationId / ContentType frame fields (DECISION-D / GAP B
        // carry), proving the republish boundary routes the same native set through the contract.
        [Fact]
        public void MustReapplyCorrelationIdAndContentTypeNativeFramesOnRepublish()
        {
            var facts = RabbitMqMessageTranslator.CaptureFacts(
                Delivered(correlationId: "corr-123", contentType: "application/json"));

            var properties = RabbitMqMessageTranslator.ToRepublishAmqp(
                facts,
                new Dictionary<string, object>(),
                new RabbitMqMessageTranslator.RepublishOptions(preserveExpiration: true, headerOverrides: null));

            properties.CorrelationId.Should().Be("corr-123");
            properties.ContentType.Should().Be("application/json");
        }

        // A delivery carrying NO native props must not stamp spurious defaults on republish.
        [Fact]
        public void MustNotStampSpuriousNativeDefaultsOnRepublishWhenNoneCarried()
        {
            var properties = RabbitMqMessageTranslator.ToRepublishAmqp(
                RabbitMqMessageTranslator.CaptureFacts(Delivered()),
                new Dictionary<string, object>(),
                new RabbitMqMessageTranslator.RepublishOptions(preserveExpiration: true, headerOverrides: null));

            properties.Expiration.Should().BeNull();
            properties.IsPriorityPresent().Should().BeFalse();
            properties.IsTimestampPresent().Should().BeFalse();
            properties.IsTypePresent().Should().BeFalse();
            properties.IsAppIdPresent().Should().BeFalse();
            properties.IsContentEncodingPresent().Should().BeFalse();
            properties.IsCorrelationIdPresent().Should().BeFalse();
        }

        // --- header-home fields: marshaller coercion still applies (the descriptor table owns only native homes) ---

        // A string-typed HEADER key (Subject) delivered as a longstr byte[] is decoded to a CLR string on receive
        // (closing GAP F — table-driven decode through the marshaller helper, no allowlist branch in the receiver).
        [Fact]
        public void MustDecodeStringTypedHeaderKeyOnReceive()
        {
            var headers = new Dictionary<string, object>
            {
                [MessageContext.Subject] = System.Text.Encoding.UTF8.GetBytes("order-subject")
            };

            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(headers: headers)),
                headers);

            coreContext[MessageContext.Subject].Should().Be("order-subject");
        }

        // An unknown header key is preserved verbatim (never force-decoded) on receive.
        [Fact]
        public void MustPreserveUnknownHeaderVerbatimOnReceive()
        {
            var binary = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var headers = new Dictionary<string, object> { ["custom-binary"] = binary };

            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(headers: headers)),
                headers);

            coreContext["custom-binary"].Should().BeEquivalentTo(binary);
        }

        // --- ExpiryTimeUtc header key: symmetric DateTime<->ISO("O") coercion (closes the encoded-only asymmetry) ---

        // A delivery carrying ExpiryTimeUtc as a UTF-8 byte[] of an ISO("O") DateTime (how a real broker surfaces the
        // ISO string the send path wrote) rehydrates to a CLR DateTime on receive, so the core's (DateTime?) cast in
        // RefreshTimeToLive does not throw. Reproduces + proves the cast-break fix.
        [Fact]
        public void MustRehydrateExpiryTimeUtcFromByteArrayOnReceive()
        {
            var expiry = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
            var headers = new Dictionary<string, object>
            {
                [MessageContext.ExpiryTimeUtc] =
                    System.Text.Encoding.UTF8.GetBytes(expiry.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            };

            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(headers: headers)),
                headers);

            coreContext[MessageContext.ExpiryTimeUtc].Should().BeOfType<DateTime>().Which.Should().Be(expiry);

            Action cast = () => _ = (DateTime?)coreContext[MessageContext.ExpiryTimeUtc];
            cast.Should().NotThrow("the core's RefreshTimeToLive (DateTime?) cast must hold after a round trip");
        }

        // send -> wire -> receive: a DateTime ExpiryTimeUtc on the outbound context is encoded to an ISO string on the
        // wire table, byte[]-ized as a real broker would, and restored to the original DateTime on receive.
        [Fact]
        public void MustRoundTripExpiryTimeUtcSendThenReceive()
        {
            var expiry = new DateTime(2026, 6, 13, 18, 30, 45, DateTimeKind.Utc);
            var message = Message(new Dictionary<string, object> { [MessageContext.ExpiryTimeUtc] = expiry });

            var sent = RabbitMqMessageTranslator.ToAmqp(message, "application/json");
            sent.Headers[MessageContext.ExpiryTimeUtc].Should().BeOfType<string>("the field table cannot encode a DateTime");

            // Model the broker surfacing the longstr header as a byte[].
            var deliveredHeaders = new Dictionary<string, object>
            {
                [MessageContext.ExpiryTimeUtc] = System.Text.Encoding.UTF8.GetBytes((string)sent.Headers[MessageContext.ExpiryTimeUtc])
            };

            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(headers: deliveredHeaders)),
                deliveredHeaders);

            coreContext[MessageContext.ExpiryTimeUtc].Should().Be(expiry);
        }

        // end-to-end: a rehydrated context feeds OutboundBrokeredMessage.RefreshTimeToLive() without throwing and
        // yields a positive TimeSpan TTL for a future expiry.
        [Fact]
        public void MustFeedRehydratedExpiryTimeUtcIntoRefreshTimeToLiveWithoutThrowing()
        {
            var expiry = DateTime.UtcNow.AddMinutes(10);
            var headers = new Dictionary<string, object>
            {
                [MessageContext.ExpiryTimeUtc] =
                    System.Text.Encoding.UTF8.GetBytes(expiry.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            };

            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(headers: headers)),
                headers);

            var outbound = new OutboundBrokeredMessage(
                Guid.NewGuid().ToString(), new byte[] { 1 }, coreContext, Destination, new RabbitMqBodyConverter());

            Action refresh = () => outbound.RefreshTimeToLive();
            refresh.Should().NotThrow();
            outbound.GetTimeToLive().Should().NotBeNull();
            outbound.GetTimeToLive().Value.Should().BeGreaterThan(TimeSpan.Zero);
        }

        // A malformed (non-ISO) ExpiryTimeUtc is DROPPED on receive (key absent), never throwing and never stamping a
        // bogus DateTime, so the core's null-guard short-circuits cleanly.
        [Fact]
        public void MustDropMalformedExpiryTimeUtcOnReceive()
        {
            var headers = new Dictionary<string, object>
            {
                [MessageContext.ExpiryTimeUtc] = System.Text.Encoding.UTF8.GetBytes("not-a-date")
            };

            (IDictionary<string, object> coreContext, string _) result = default;
            Action translate = () => result = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(headers: headers)),
                headers);

            translate.Should().NotThrow();
            result.coreContext.Should().NotContainKey(MessageContext.ExpiryTimeUtc);
        }

        // General symmetry over the public translate seam: for the non-string ExpiryTimeUtc descriptor and a
        // representative string-typed descriptor, the encoded-then-decoded value restores the original CLR type and
        // value (exercising ToAmqp/ToCore rather than reflecting over the private table).
        [Fact]
        public void MustRestoreOriginalClrTypeForEachDescriptorThroughTranslateSeam()
        {
            var expiry = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var message = Message(new Dictionary<string, object>
            {
                [MessageContext.ExpiryTimeUtc] = expiry,
                [MessageContext.Subject] = "order-subject"
            });

            var sent = RabbitMqMessageTranslator.ToAmqp(message, "application/json");

            // Model the broker surfacing each longstr header as a byte[].
            var deliveredHeaders = new Dictionary<string, object>
            {
                [MessageContext.ExpiryTimeUtc] = System.Text.Encoding.UTF8.GetBytes((string)sent.Headers[MessageContext.ExpiryTimeUtc]),
                [MessageContext.Subject] = System.Text.Encoding.UTF8.GetBytes((string)sent.Headers[MessageContext.Subject])
            };

            var (coreContext, _) = RabbitMqMessageTranslator.ToCore(
                RabbitMqMessageTranslator.CaptureFacts(Delivered(headers: deliveredHeaders)),
                deliveredHeaders);

            coreContext[MessageContext.ExpiryTimeUtc].Should().BeOfType<DateTime>().Which.Should().Be(expiry);
            coreContext[MessageContext.Subject].Should().BeOfType<string>().Which.Should().Be("order-subject");
        }

        [Fact]
        public void MustThrowWhenSendMessageNull()
        {
            Action act = () => RabbitMqMessageTranslator.ToAmqp(null, "application/json");
            act.Should().Throw<ArgumentNullException>();
        }
    }
}
