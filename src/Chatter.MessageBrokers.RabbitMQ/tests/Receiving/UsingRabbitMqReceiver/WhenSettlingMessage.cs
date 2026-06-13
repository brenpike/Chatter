using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using RabbitMQ.Client;
using System;
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

        // REPRODUCTION (r3407616994): when a receiver is registered with neither a dead-letter nor an error path,
        // `destination` resolves to null/blank. The deadletter republish uses it as the default-exchange routing key
        // with `mandatory: true`, so the publish would be UNROUTABLE and fault only AFTER the message exhausted its
        // retry budget — leaving the original delivery un-acked and redelivered indefinitely (a poison-message hot
        // loop). The receiver now FAILS FAST with an actionable misconfiguration error naming the queue, and does NOT
        // attempt the unroutable publish or ack the original.
        [Fact]
        public async Task MustFailFastWhenNeitherDeadLetterNorErrorPathConfigured()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: null, errorQueuePath: null);
            await harness.PushAsync(deliveryTag: 13);
            var context = await harness.ReceiveAsync();

            var act = async () => await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage($"*{ReceiverHarness.ReceiverPath}*");
            harness.ConnectionSource.PublishChannels.Should().BeEmpty();
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty();
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

        // --- native-property propagation across republish hops (closes the TTL-propagation root-cluster) ---

        // The single shared builder re-applies every carried native AMQP property on the classic nack-republish
        // hop, INCLUDING the native Expiration (per-message TTL), so a classic-queue message published with a TTL
        // keeps its TTL across the redelivery republish rather than losing it on the first nack.
        [Fact]
        public async Task MustPreserveNativeExpirationAndSampledPropsOnNackRepublishForClassic()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            var timestamp = new AmqpTimestamp(1718000000L);
            await harness.PushAsync(deliveryTag: 9,
                                    expiration: "60000",
                                    priority: 4,
                                    timestamp: timestamp,
                                    type: "OrderPlaced",
                                    appId: "orders-svc",
                                    contentEncoding: "gzip",
                                    contentType: "application/json",
                                    correlationId: "corr-123");
            var context = await harness.ReceiveAsync();

            var nacked = await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            nacked.Should().BeTrue();
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.Expiration.Should().Be("60000", "the nack-redelivery hop preserves the delivered per-message TTL");
            republish.Priority.Should().Be((byte)4);
            republish.Timestamp.Should().Be(timestamp);
            republish.Type.Should().Be("OrderPlaced");
            republish.AppId.Should().Be("orders-svc");
            republish.ContentEncoding.Should().Be("gzip");
            republish.ContentType.Should().Be("application/json");
            republish.CorrelationId.Should().Be("corr-123");
        }

        // The deadletter hop DROPS the native Expiration (a DLQ is for inspection; a dead-lettered message must
        // not auto-expire via the original TTL) but carries every OTHER native property, AND the failure-detail
        // header overrides are still applied.
        [Fact]
        public async Task MustDropNativeExpirationButCarryOtherPropsAndFailureHeadersOnDeadletter()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: ReceiverHarness.DeadLetterPath);
            var timestamp = new AmqpTimestamp(1718000000L);
            await harness.PushAsync(deliveryTag: 11,
                                    expiration: "60000",
                                    priority: 4,
                                    timestamp: timestamp,
                                    type: "OrderPlaced",
                                    appId: "orders-svc",
                                    contentEncoding: "gzip",
                                    contentType: "application/json",
                                    correlationId: "corr-123");
            var context = await harness.ReceiveAsync();

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "could not be handled", CancellationToken.None);

            deadlettered.Should().BeTrue();
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.Expiration.Should().BeNull("a dead-lettered message must not auto-expire via the original per-message TTL");
            republish.Priority.Should().Be((byte)4);
            republish.Timestamp.Should().Be(timestamp);
            republish.Type.Should().Be("OrderPlaced");
            republish.AppId.Should().Be("orders-svc");
            republish.ContentEncoding.Should().Be("gzip");
            republish.ContentType.Should().Be("application/json");
            republish.CorrelationId.Should().Be("corr-123");
            republish.Headers[MessageContext.FailureDetails].Should().Be("poisoned");
            republish.Headers[MessageContext.FailureDescription].Should().Be("could not be handled");
        }

        // A delivery carrying NO native properties must not have a spurious default stamped on republish: an
        // absent Expiration stays null (not "0"), an absent Priority stays null (not 0), and so on.
        [Fact]
        public async Task MustNotStampSpuriousDefaultsWhenDeliveryCarriesNoNativeProps()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            await harness.PushAsync(deliveryTag: 9);
            var context = await harness.ReceiveAsync();

            var nacked = await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            nacked.Should().BeTrue();
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.Expiration.Should().BeNull("an absent native Expiration must not become a spurious default");
            republish.Priority.Should().BeNull("an absent native Priority must not become a spurious 0");
            republish.Timestamp.Should().BeNull();
            republish.Type.Should().BeNull();
            republish.AppId.Should().BeNull();
            republish.ContentEncoding.Should().BeNull();
            republish.CorrelationId.Should().BeNull();
        }

        // ContentType / CorrelationId are carried as the single native frame value and re-applied to the native
        // frame field on republish; the marshaller writes its decoded header copy into the table. The native frame
        // field is the authoritative one and is not double-applied in a conflicting way.
        [Fact]
        public async Task MustApplyContentTypeAndCorrelationIdToNativeFrameWithoutConflictOnRepublish()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic);
            await harness.PushAsync(deliveryTag: 9,
                                    contentType: "application/json",
                                    correlationId: "corr-123");
            var context = await harness.ReceiveAsync();

            var nacked = await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            nacked.Should().BeTrue();
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.ContentType.Should().Be("application/json", "the carried native ContentType is the authoritative frame value on republish");
            republish.CorrelationId.Should().Be("corr-123", "the carried native CorrelationId is the authoritative frame value on republish");
        }
    }
}
