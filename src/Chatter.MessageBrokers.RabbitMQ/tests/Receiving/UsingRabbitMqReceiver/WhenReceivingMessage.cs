using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
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

        [Fact]
        public async Task MustSurfaceExchangeAndRoutingKeyOnContext()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 1);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[RabbitMqMessageContext.TargetExchange].Should().Be("");
            context.BrokeredMessage.MessageContext[RabbitMqMessageContext.RoutingKey].Should().Be(ReceiverHarness.ReceiverPath);
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

        // Classic: attempts == adapter x-chatter-delivery-count (0 when absent on first delivery).
        [Fact]
        public async Task MustStampClassicAttemptsAsZeroWhenNoAdapterHeader()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            await harness.PushAsync(deliveryTag: 1);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(0);
        }

        [Fact]
        public async Task MustStampClassicAttemptsFromAdapterHeader()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var headers = new Dictionary<string, object> { [RabbitMqMessageContext.DeliveryCountHeader] = 2 };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(2);
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

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(7);
        }

        // --- Untrusted delivery-count header hardening ---
        // The delivery-count headers are broker-supplied and hostile: a negative or oversized value must never
        // stamp a negative or wrapped ReceiveAttempts (which the core compares to MaxReceiveAttempts, so a poison
        // value could dodge deadlettering). The receiver saturates the raw count into [0, int.MaxValue].

        // Classic: a negative adapter header is clamped to 0 rather than stamped as a negative attempt count.
        [Fact]
        public async Task MustClampNegativeClassicAdapterHeaderToZero()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var headers = new Dictionary<string, object> { [RabbitMqMessageContext.DeliveryCountHeader] = -5L };
            await harness.PushAsync(deliveryTag: 1, headers: headers);

            var context = await harness.ReceiveAsync();

            context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts].Should().Be(0);
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
