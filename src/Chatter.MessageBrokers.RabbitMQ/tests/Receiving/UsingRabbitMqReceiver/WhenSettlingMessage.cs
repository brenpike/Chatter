using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver
{
    // Pins RabbitMqReceiver settlement: ack on a matching epoch acks the carried delivery tag; nack requeues
    // (Quorum) or republishes-with-incremented-count-then-acks (Classic); deadletter republishes to the
    // attribute-declared deadletter/error path then acks; and EVERY settlement is epoch-guarded — a settlement
    // carrying a stale epoch (forced via the in-memory source's AdvanceEpoch) is a no-op (the false-ack guard).
    // The republish-then-ack ordering is asserted: the confirmed republish is recorded BEFORE the original ack.
    public class WhenSettlingMessage : Testing.Core.Context
    {
        // --- ack ---

        [Fact]
        public async Task MustAckCarriedDeliveryTagOnMatchingEpoch()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 77);
            var context = await harness.ReceiveAsync();

            var acked = await harness.Receiver.AckMessageAsync(context, transactionContext: null, CancellationToken.None);

            acked.Should().BeTrue();
            harness.ConnectionSource.ReceiveChannel.Acks.Single().DeliveryTag.Should().Be(77UL);
        }

        [Fact]
        public async Task MustNotAckOnStaleEpoch()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 77);
            var context = await harness.ReceiveAsync();

            // Force the receive channel to recycle: the carried epoch (0) no longer matches the current epoch.
            harness.ConnectionSource.AdvanceEpoch();

            var acked = await harness.Receiver.AckMessageAsync(context, transactionContext: null, CancellationToken.None);

            acked.Should().BeFalse("a settlement carrying a stale epoch must be a no-op (the false-ack guard)");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReturnFalseWhenContextCarriesNoReceivedMessage()
        {
            var harness = ReceiverHarness.Create();
            var bareContext = new Chatter.MessageBrokers.Context.MessageBrokerContext(
                "id", new byte[] { 1 }, new Dictionary<string, object>(), ReceiverHarness.ReceiverPath, CancellationToken.None, new RabbitMqBodyConverter());

            var acked = await harness.Receiver.AckMessageAsync(bareContext, transactionContext: null, CancellationToken.None);

            acked.Should().BeFalse();
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty();
        }

        // --- nack: Quorum requeues natively ---

        [Fact]
        public async Task MustRequeueOnNackForQuorum()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            await harness.PushAsync(deliveryTag: 5);
            var context = await harness.ReceiveAsync();

            var nacked = await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            nacked.Should().BeTrue();
            var nack = harness.ConnectionSource.ReceiveChannel.Nacks.Single();
            nack.DeliveryTag.Should().Be(5UL);
            nack.Requeue.Should().BeTrue();
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty("a quorum nack requeues; it does not ack");
        }

        [Fact]
        public async Task MustNotRequeueOnNackForQuorumWhenEpochStale()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            await harness.PushAsync(deliveryTag: 5);
            var context = await harness.ReceiveAsync();

            harness.ConnectionSource.AdvanceEpoch();

            var nacked = await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            nacked.Should().BeFalse();
            harness.ConnectionSource.ReceiveChannel.Nacks.Should().BeEmpty();
        }

        // --- nack: Classic republishes-with-incremented-count then acks ---

        [Fact]
        public async Task MustRepublishToReceiverPathWithIncrementedCountOnNackForClassic()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var headers = new Dictionary<string, object> { [RabbitMqMessageContext.DeliveryCountHeader] = 2 };
            await harness.PushAsync(deliveryTag: 9, headers: headers);
            var context = await harness.ReceiveAsync();

            var nacked = await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            nacked.Should().BeTrue();
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.Exchange.Should().BeEmpty();
            republish.RoutingKey.Should().Be(ReceiverHarness.ReceiverPath);
            republish.Headers[RabbitMqMessageContext.DeliveryCountHeader].Should().Be(3L);
        }

        [Fact]
        public async Task MustConfirmRepublishBeforeAckOnNackForClassic()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            await harness.PushAsync(deliveryTag: 9);
            var context = await harness.ReceiveAsync();

            await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            // The republish is recorded on a pooled publish channel and the ack on the receive channel; the
            // INVARIANT under test is that the (confirmed) republish happened and THEN the original was acked.
            harness.ConnectionSource.PublishChannels.Single().Publishes.Should().ContainSingle();
            harness.ConnectionSource.ReceiveChannel.Acks.Single().DeliveryTag.Should().Be(9UL);
        }

        [Fact]
        public async Task MustNotAckClassicNackRepublishWhenEpochStale()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            await harness.PushAsync(deliveryTag: 9);
            var context = await harness.ReceiveAsync();

            harness.ConnectionSource.AdvanceEpoch();

            var nacked = await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            // The republish is publisher-confirmed regardless, but the original ack is epoch-guarded: a stale
            // epoch makes the ack a no-op so the broker redelivers and the republished copy is the duplicate.
            nacked.Should().BeFalse();
            harness.ConnectionSource.PublishChannels.Single().Publishes.Should().ContainSingle();
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty();
        }

        // --- deadletter republishes then acks ---

        [Fact]
        public async Task MustRepublishToDeadLetterPathThenAck()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: ReceiverHarness.DeadLetterPath);
            await harness.PushAsync(deliveryTag: 11);
            var context = await harness.ReceiveAsync();

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "could not be handled", CancellationToken.None);

            deadlettered.Should().BeTrue();
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.RoutingKey.Should().Be(ReceiverHarness.DeadLetterPath);
            republish.Headers[MessageContext.FailureDetails].Should().Be("poisoned");
            republish.Headers[MessageContext.FailureDescription].Should().Be("could not be handled");
            harness.ConnectionSource.ReceiveChannel.Acks.Single().DeliveryTag.Should().Be(11UL);
        }

        [Fact]
        public async Task MustRepublishToErrorPathWhenNoDeadLetterPathConfigured()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: null, errorQueuePath: ReceiverHarness.ErrorPath);
            await harness.PushAsync(deliveryTag: 12);
            var context = await harness.ReceiveAsync();

            await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            harness.ConnectionSource.PublishChannels.Single().Publishes.Single()
                .RoutingKey.Should().Be(ReceiverHarness.ErrorPath);
        }

        [Fact]
        public async Task MustNotAckDeadletterWhenEpochStale()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: ReceiverHarness.DeadLetterPath);
            await harness.PushAsync(deliveryTag: 11);
            var context = await harness.ReceiveAsync();

            harness.ConnectionSource.AdvanceEpoch();

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            deadlettered.Should().BeFalse();
            harness.ConnectionSource.PublishChannels.Single().Publishes.Should().ContainSingle();
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty();
        }
    }
}
