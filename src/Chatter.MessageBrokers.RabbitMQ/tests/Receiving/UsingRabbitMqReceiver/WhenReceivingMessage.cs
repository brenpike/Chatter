using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver
{
    // Pins RabbitMqReceiver.ReceiveMessageAsync: a delivery pushed through the consumer is pulled with its
    // delivery tag, channel epoch, exchange, routing key, infrastructure type, and decoded body surfaced on the
    // MessageBrokerContext, and ReceiveAttempts is STAMPED as an int for both the Quorum (native
    // x-delivery-count) and Classic (x-chatter-delivery-count) paths. No live broker — the in-memory source's
    // PushDeliveryAsync raises the registered consumer.
    public class WhenReceivingMessage : Testing.Core.Context
    {
        [Fact]
        public async Task MustSurfaceDeliveryTagOnContext()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 42);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[RabbitMqMessageContext.DeliveryTag].Should().Be(42UL);
        }

        // REPRODUCTION (r3407975403): AMQP prefetch is a ushort on the wire. A configured Prefetch above 65,535 would
        // WRAP on the (ushort) cast at the BasicQosAsync call site — e.g. 65,536 -> 0, which RabbitMQ reads as
        // UNLIMITED prefetch, silently removing all backpressure. The resolved prefetch is now clamped to
        // [1, ushort.MaxValue] BEFORE the cast, so an over-large configuration saturates at the maximum supported
        // prefetch instead of wrapping to 0.
        [Fact]
        public void MustClampPrefetchAboveUShortMaxBeforeQosCastSoItDoesNotWrapToUnlimited()
        {
            var harness = ReceiverHarness.Create(prefetch: 65_536);

            harness.ConnectionSource.ReceiveChannel.LastQosPrefetchCount
                .Should().Be(ushort.MaxValue, "a prefetch above ushort.MaxValue must saturate, never wrap to 0 (unlimited)");
        }

        // MaxConcurrentCalls is floored into the prefetch as well, so an over-large MaxConcurrentCalls is clamped
        // by the same guard rather than wrapping the QoS cast.
        [Fact]
        public void MustClampPrefetchWhenMaxConcurrentCallsExceedsUShortMax()
        {
            var harness = ReceiverHarness.Create(maxConcurrentCalls: 70_000);

            harness.ConnectionSource.ReceiveChannel.LastQosPrefetchCount
                .Should().Be(ushort.MaxValue);
        }

        // A normal in-range prefetch is unaffected by the clamp: it reaches QoS exactly as configured.
        [Fact]
        public void MustPassThroughInRangePrefetchUnchanged()
        {
            var harness = ReceiverHarness.Create(prefetch: 50);

            harness.ConnectionSource.ReceiveChannel.LastQosPrefetchCount
                .Should().Be((ushort)50);
        }

        [Fact]
        public async Task MustSurfaceChannelEpochOnContext()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1);

            var context = await harness.ReceiveAsync();

            // The registration-time epoch (0) is carried on the delivery and stamped onto the context.
            context.BrokeredMessage.MessageContext[RabbitMqMessageContext.ChannelEpoch].Should().Be(0L);
        }

        // REGRESSION: the broker-supplied inbound delivery exchange / routing key must NOT leak into the OUTBOUND
        // routing-override command keys (TargetExchange / RoutingKey). The core seeds an outbound send's options
        // from the inbound context, so if these were stamped from the inbound delivery a receive-then-send
        // follow-up would be silently re-routed back toward the inbound queue. Only WithRabbitMqRouting may set them.
        [Fact]
        public async Task MustNotLeakInboundExchangeIntoOutboundRoutingKeys()
        {
            var harness = ReceiverHarness.Create();
            await harness.ConnectionSource.PushDeliveryAsync(
                deliveryTag: 1,
                body: new byte[] { 1, 2, 3 },
                exchange: "inbound-exchange",
                routingKey: "inbound.routing.key");

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.TargetExchange);
            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.RoutingKey);
        }

        // Default-exchange (exchange "") delivery: the command keys are still left unset on the inbound context.
        [Fact]
        public async Task MustNotStampOutboundRoutingKeysForDefaultExchangeDelivery()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.TargetExchange);
            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.RoutingKey);
        }

        // An EXPLICIT WithRabbitMqRouting override on a follow-up outbound message survives now that no inbound
        // delivery value clobbers the command keys.
        [Fact]
        public async Task MustPreserveExplicitWithRabbitMqRoutingOverrideAfterReceive()
        {
            var harness = ReceiverHarness.Create();
            await harness.ConnectionSource.PushDeliveryAsync(
                deliveryTag: 1,
                body: new byte[] { 1, 2, 3 },
                exchange: "inbound-exchange",
                routingKey: "inbound.routing.key");
            await harness.ReceiveAsync();

            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json; charset=utf-8");
            var outbound = new OutboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", bodyConverter.Object);

            outbound.WithRabbitMqRouting("orders-exchange", "orders.created");

            outbound.MessageContext[RabbitMqMessageContext.TargetExchange].Should().Be("orders-exchange");
            outbound.MessageContext[RabbitMqMessageContext.RoutingKey].Should().Be("orders.created");
        }

        // BEHAVIOR: inbound AMQP application headers (custom / correlation context the publisher stamped) survive
        // the first receive hop instead of being discarded, matching the other adapters that copy inbound
        // application properties before adding infrastructure stamps. These are UNKNOWN (not core string-typed)
        // keys, so the marshaller preserves them VERBATIM — pushed here pre-wire (no longstr coercion) to assert
        // the value-preservation contract directly.
        [Fact]
        public async Task MustPreserveInboundApplicationHeadersOnContext()
        {
            var harness = ReceiverHarness.Create();
            var headers = new Dictionary<string, object>
            {
                ["x-correlation-id"] = "corr-123",
                ["custom-app-header"] = "app-value"
            };
            await harness.PushVerbatimAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext["x-correlation-id"].Should().Be("corr-123");
            context.BrokeredMessage.MessageContext["custom-app-header"].Should().Be("app-value");
        }

        // BEHAVIOR: an UNKNOWN-key string header that arrives as a byte[] (the AMQP longstr coercion a real broker
        // performs) is preserved VERBATIM as a byte[] — the marshaller never force-decodes an unknown key, so a
        // genuine binary header is never corrupted. Only the documented core string-typed keys are decoded.
        [Fact]
        public async Task MustPreserveUnknownByteArrayHeadersVerbatim()
        {
            var harness = ReceiverHarness.Create();
            var binary = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var headers = new Dictionary<string, object> { ["custom-binary-header"] = binary };
            await harness.PushVerbatimAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext["custom-binary-header"].Should().BeEquivalentTo(binary);
        }

        // REGRESSION: copying inbound headers must NOT reintroduce the outbound routing-override command keys. A
        // round-tripped message can carry TargetExchange / RoutingKey because the sender publishes the full context
        // as headers; if preserved on receive, the core would re-route a receive-then-send follow-up back toward the
        // inbound queue. The receiver strips them while still preserving other inbound application headers.
        [Fact]
        public async Task MustStripInboundRoutingOverrideKeysWhilePreservingOtherHeaders()
        {
            var harness = ReceiverHarness.Create();
            var headers = new Dictionary<string, object>
            {
                [RabbitMqMessageContext.TargetExchange] = "stale-exchange",
                [RabbitMqMessageContext.RoutingKey] = "stale.routing.key",
                ["x-correlation-id"] = "corr-123"
            };
            await harness.PushVerbatimAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.TargetExchange);
            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.RoutingKey);
            context.BrokeredMessage.MessageContext["x-correlation-id"].Should().Be("corr-123");
        }

        // REGRESSION: the adapter-owned delivery-count header (classic x-chatter-delivery-count) must NOT survive
        // onto the emitted context. The core seeds a receive-then-send follow-up's options from this context and the
        // sender republishes the full context as headers; a surviving counter would be re-stamped onto the outbound
        // message and read back by ResolveReceiveAttempts on the next classic queue's first delivery as a stale
        // redelivery, deadlettering a fresh message with too few attempts. ReceiveAttempts is still computed from the
        // inbound value (3 prior deliveries -> attempt 4) before the strip; other inbound headers are preserved.
        [Fact]
        public async Task MustStripClassicDeliveryCountHeaderWhilePreservingOtherHeaders()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var headers = new Dictionary<string, object>
            {
                [RabbitMqMessageContext.DeliveryCountHeader] = 3L,
                ["x-correlation-id"] = "corr-123"
            };
            await harness.PushVerbatimAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.DeliveryCountHeader,
                "the adapter delivery counter must not ride the inbound context onto an outbound republish");
            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(4,
                "ReceiveAttempts is still resolved from the inbound counter (3 prior + 1) before the header is stripped");
            context.BrokeredMessage.MessageContext["x-correlation-id"].Should().Be("corr-123");
        }

        // REGRESSION: the native quorum delivery-count header (x-delivery-count) must likewise NOT survive onto the
        // emitted context, so it cannot be re-stamped onto an outbound send and misread as a redelivery on the next
        // queue. ReceiveAttempts is still resolved from it (5 prior -> attempt 6) before the strip.
        [Fact]
        public async Task MustStripNativeDeliveryCountHeaderWhilePreservingOtherHeaders()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            var headers = new Dictionary<string, object>
            {
                [ReceiverHarness.NativeDeliveryCountHeader] = 5L,
                ["x-correlation-id"] = "corr-456"
            };
            await harness.PushVerbatimAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext.Should().NotContainKey(ReceiverHarness.NativeDeliveryCountHeader,
                "the native delivery counter must not ride the inbound context onto an outbound republish");
            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(6,
                "ReceiveAttempts is still resolved from the native counter (5 prior + 1) before the header is stripped");
            context.BrokeredMessage.MessageContext["x-correlation-id"].Should().Be("corr-456");
        }

        // P1 REPRODUCTION: a real broker delivers the string CorrelationId application header as an AMQP longstr
        // (byte[]). The core's InboundBrokeredMessage casts MessageContext.CorrelationId straight to (string) at
        // construction (which MessageBrokerContext does inside ReceiveMessageAsync), so before the marshaller a
        // byte[] CorrelationId threw InvalidCastException on a self-published round-trip BEFORE the handler ran.
        // The marshaller decodes the known string-typed key back to string, so the receive no longer throws and
        // the decoded value is surfaced.
        [Fact]
        public async Task MustDecodeByteArrayCorrelationIdSoReceiveDoesNotThrow()
        {
            var harness = ReceiverHarness.Create();
            var headers = new Dictionary<string, object> { [MessageContext.CorrelationId] = "corr-round-trip" };
            // Default coercion models the broker: the string CorrelationId is delivered as a UTF-8 byte[].
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            // Before the fix this threw InvalidCastException inside ReceiveMessageAsync -> InboundBrokeredMessage.
            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.CorrelationId].Should().Be("corr-round-trip");
            context.BrokeredMessage.CorrelationId.Should().Be("corr-round-trip");
        }

        // A custom string-typed core key (Subject) delivered as a byte[] longstr is likewise decoded to string by
        // the marshaller, proving the decode covers the full documented known-string-typed-key set, not only
        // CorrelationId.
        [Fact]
        public async Task MustDecodeByteArrayForKnownStringTypedKeys()
        {
            var harness = ReceiverHarness.Create();
            var headers = new Dictionary<string, object> { [MessageContext.Subject] = "order-subject" };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.Subject].Should().Be("order-subject");
        }

        // The freshly-stamped infrastructure keys win over any inbound header carrying the same key, so a
        // round-tripped delivery cannot poison DeliveryTag / ChannelEpoch / InfrastructureType / ReceiveAttempts.
        [Fact]
        public async Task MustOverwriteInboundInfrastructureKeysWithFreshStamps()
        {
            var harness = ReceiverHarness.Create();
            var headers = new Dictionary<string, object>
            {
                [RabbitMqMessageContext.DeliveryTag] = 999UL,
                [RabbitMqMessageContext.ChannelEpoch] = 999L,
                [MessageContext.InfrastructureType] = "stale-infrastructure-type"
            };
            await harness.PushAsync(deliveryTag: 7, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[RabbitMqMessageContext.DeliveryTag].Should().Be(7UL);
            context.BrokeredMessage.MessageContext[RabbitMqMessageContext.ChannelEpoch].Should().Be(0L);
            context.BrokeredMessage.MessageContext[MessageContext.InfrastructureType]
                .Should().Be(RabbitMqMessageContext.InfrastructureType);
        }

        [Fact]
        public async Task MustStampRabbitMqInfrastructureTypeOnContext()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.InfrastructureType]
                .Should().Be(RabbitMqMessageContext.InfrastructureType);
        }

        [Fact]
        public async Task MustDecodeBodyOntoContext()
        {
            var harness = ReceiverHarness.Create();
            var body = new byte[] { 5, 6, 7, 8 };
            await harness.PushAsync(deliveryTag: 1, body: body);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.Body.Should().Equal(body);
        }

        // BEHAVIOR: the emitted context carries the converter the core factory resolves for the configured
        // MessageBodyType (default JSON), so GetMessageFromBody round-trips a UTF-8 JSON payload — proving the
        // option drives deserialization rather than a hardwired concrete converter.
        [Fact]
        public async Task MustDeserializeBodyViaFactoryResolvedConverterForConfiguredBodyType()
        {
            var harness = ReceiverHarness.Create();
            var jsonBody = System.Text.Encoding.UTF8.GetBytes("{\"Name\":\"orders\"}");
            await harness.PushAsync(deliveryTag: 1, body: jsonBody);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.GetMessageFromBody<BodyPayload>().Name.Should().Be("orders");
        }

        private sealed class BodyPayload
        {
            public string Name { get; set; }
        }

        // GAP B: when the delivery carries a native ContentType, it is surfaced onto the context so the receiver
        // selects the inbound body converter from the DELIVERED content-type rather than always the configured
        // MessageBodyType. The harness registers the RabbitMqBodyConverter ("application/json; charset=utf-8"); a
        // delivery advertising that content type round-trips a JSON body, proving the delivered type drove selection.
        [Fact]
        public async Task MustSurfaceDeliveredContentTypeAndSelectConverterFromIt()
        {
            var harness = ReceiverHarness.Create();
            var jsonBody = System.Text.Encoding.UTF8.GetBytes("{\"Name\":\"orders\"}");
            await harness.PushAsync(deliveryTag: 1, body: jsonBody, contentType: "application/json; charset=utf-8");

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ContentType].Should().Be("application/json; charset=utf-8");
            context.BrokeredMessage.GetMessageFromBody<ContentTypePayload>().Name.Should().Be("orders");
        }

        // GAP B fallback: a delivery whose native content-type is UNKNOWN to the factory still receives without
        // throwing — the factory falls back to the core JsonBodyConverter, preserving the unknown-content-type
        // fallback rather than failing the receive.
        [Fact]
        public async Task MustFallBackToJsonConverterWhenDeliveredContentTypeUnknown()
        {
            var harness = ReceiverHarness.Create();
            var jsonBody = System.Text.Encoding.UTF8.GetBytes("{\"Name\":\"orders\"}");
            await harness.PushAsync(deliveryTag: 1, body: jsonBody, contentType: "application/x-unknown");

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.GetMessageFromBody<ContentTypePayload>().Name.Should().Be("orders");
        }

        private sealed class ContentTypePayload
        {
            public string Name { get; set; }
        }

        // GAP A: a delivered native Expiration (per-message TTL, ms string) is reconstituted onto the received
        // context as a TimeSpan MessageContext.TimeToLive, so the core concept survives the receive hop (a
        // downstream re-send can read it). Before the single translator the receive path discarded it entirely.
        [Fact]
        public async Task MustReconstituteNativeExpirationIntoTimeToLiveOnReceive()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1, expiration: "1500");

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.TimeToLive].Should().Be(System.TimeSpan.FromMilliseconds(1500));
        }

        // The C-family carry-only natives (Priority / Timestamp / Type / AppId / ContentEncoding) are NEVER
        // surfaced into the received core context (DECISION-B) even when the delivery carried them — they exist
        // only as republish-carry facts on the ReceivedMessage.
        [Fact]
        public async Task MustNotSurfaceCarryOnlyNativePropsIntoReceivedContext()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1,
                                    priority: 4,
                                    timestamp: new global::RabbitMQ.Client.AmqpTimestamp(1718000000L),
                                    type: "OrderPlaced",
                                    appId: "orders-svc",
                                    contentEncoding: "gzip");

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext.Should().NotContainKey("Priority");
            context.BrokeredMessage.MessageContext.Should().NotContainKey("Timestamp");
            context.BrokeredMessage.MessageContext.Should().NotContainKey("Type");
            context.BrokeredMessage.MessageContext.Should().NotContainKey("AppId");
            context.BrokeredMessage.MessageContext.Should().NotContainKey("ContentEncoding");
        }

        [Fact]
        public async Task MustUseBrokerMessageIdWhenPresent()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1, messageId: "broker-message-id");

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageId.Should().Be("broker-message-id");
        }

        [Fact]
        public async Task MustGenerateMessageIdWhenAbsent()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1, messageId: null);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageId.Should().NotBeNullOrEmpty();
        }

        // --- ReceiveAttempts stamping (the MANDATORY int the core's default MessageDeliveryCountAsync casts) ---

        [Fact]
        public async Task MustStampReceiveAttemptsAsInt()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().BeOfType<int>();
        }

        // Quorum: attempts == native x-delivery-count + 1. On first delivery the header is absent -> 1.
        [Fact]
        public async Task MustStampQuorumAttemptsAsOneWhenNoNativeHeader()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            await harness.PushAsync(deliveryTag: 1);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(1);
        }

        [Fact]
        public async Task MustStampQuorumAttemptsAsNativeCountPlusOne()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            var headers = new Dictionary<string, object> { [ReceiverHarness.NativeDeliveryCountHeader] = 3 };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(4);
        }

        // The native count arrives as a long-typed header in some clients; the receiver reads it tolerantly.
        [Fact]
        public async Task MustStampQuorumAttemptsWhenNativeCountIsLongTyped()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            var headers = new Dictionary<string, object> { [ReceiverHarness.NativeDeliveryCountHeader] = 5L };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(6);
        }

        // Classic: attempts == adapter x-chatter-delivery-count (prior deliveries) + 1, so the first delivery
        // (header absent -> 0 prior) is attempt 1 — matching quorum and the shared receiver contract, where an
        // actual delivery is at least the first attempt.
        [Fact]
        public async Task MustStampClassicAttemptsAsOneWhenNoAdapterHeader()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            await harness.PushAsync(deliveryTag: 1);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(1);
        }

        [Fact]
        public async Task MustStampClassicAttemptsFromAdapterHeaderPlusOne()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var headers = new Dictionary<string, object> { [RabbitMqMessageContext.DeliveryCountHeader] = 2 };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            // 2 prior deliveries -> attempt 3.
            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(3);
        }

        // The adapter header survives as a UTF-8 byte[] across a hop; the receiver parses it tolerantly.
        [Fact]
        public async Task MustStampClassicAttemptsWhenAdapterHeaderIsByteArray()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var headers = new Dictionary<string, object>
            {
                [RabbitMqMessageContext.DeliveryCountHeader] = System.Text.Encoding.UTF8.GetBytes("7")
            };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            // 7 prior deliveries -> attempt 8.
            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(8);
        }

        // --- Untrusted delivery-count header hardening ---
        // The delivery-count headers are broker-supplied and hostile: a negative or oversized value must never
        // stamp a negative or wrapped ReceiveAttempts (which the core compares to MaxReceiveAttempts, so a poison
        // value could dodge deadlettering). The receiver saturates the raw count into [0, int.MaxValue].

        // Classic: a negative adapter header is clamped to 0 prior deliveries, so attempts floors at 1 (this
        // delivery) rather than being stamped as a negative attempt count.
        [Fact]
        public async Task MustFloorNegativeClassicAdapterHeaderToOne()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var headers = new Dictionary<string, object> { [RabbitMqMessageContext.DeliveryCountHeader] = -5L };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(1);
        }

        // Classic: a value above int.MaxValue saturates instead of wrapping to a negative via the long->int cast.
        [Fact]
        public async Task MustSaturateOversizedClassicAdapterHeader()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var headers = new Dictionary<string, object> { [RabbitMqMessageContext.DeliveryCountHeader] = (long)int.MaxValue + 100L };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(int.MaxValue);
        }

        // Quorum: a negative native count clamps to 0 prior redeliveries, so attempts floors at 1 (this delivery).
        [Fact]
        public async Task MustFloorQuorumAttemptsAtOneWhenNativeCountNegative()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            var headers = new Dictionary<string, object> { [ReceiverHarness.NativeDeliveryCountHeader] = -3L };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(1);
        }

        // Quorum: an int.MaxValue native count saturates rather than overflowing to a negative on the +1.
        [Fact]
        public async Task MustSaturateQuorumAttemptsWhenNativeCountAtMax()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            var headers = new Dictionary<string, object> { [ReceiverHarness.NativeDeliveryCountHeader] = (long)int.MaxValue };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(int.MaxValue);
        }
    }
}
