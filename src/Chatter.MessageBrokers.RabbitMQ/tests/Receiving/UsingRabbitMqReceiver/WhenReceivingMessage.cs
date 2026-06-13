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
        // application properties before adding infrastructure stamps.
        [Fact]
        public async Task MustPreserveInboundApplicationHeadersOnContext()
        {
            var harness = ReceiverHarness.Create();
            var headers = new Dictionary<string, object>
            {
                ["x-correlation-id"] = "corr-123",
                ["custom-app-header"] = "app-value"
            };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext["x-correlation-id"].Should().Be("corr-123");
            context.BrokeredMessage.MessageContext["custom-app-header"].Should().Be("app-value");
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
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.TargetExchange);
            context.BrokeredMessage.MessageContext.Should().NotContainKey(RabbitMqMessageContext.RoutingKey);
            context.BrokeredMessage.MessageContext["x-correlation-id"].Should().Be("corr-123");
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
