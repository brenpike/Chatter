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
    // (Quorum) or republishes-with-incremented-count-then-acks (Classic); deadletter republishes-confirmed to the
    // attribute-declared deadletter/error path then acks (DLQ path returns true so the core ALSO forwards an
    // error-queue copy; error-only path returns FALSE to suppress the core's ErrorQueueDispatcher so exactly one
    // error-queue copy exists); and EVERY settlement is epoch-guarded — a settlement carrying a stale epoch (forced
    // via the in-memory source's AdvanceEpoch) makes the ACK a no-op (the false-ack guard) while the republish is
    // publisher-confirmed regardless, so the durable copy survives and the broker redelivers the original.
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

        // OWNERSHIP (r3408649034): the dead-letter queue is the ADAPTER's responsibility; the ERROR queue is the
        // CORE's. On max-receives the core runs deadletter FIRST and, ONLY when it returns true, ALSO runs its
        // error-recovery action (ErrorQueueDispatcher → IForwardMessages) which forwards the inbound message to the
        // error queue via this same RabbitMQ infrastructure. For an ERROR-ONLY config (no dead-letter queue) the
        // adapter republishes-confirmed the single durable copy to the error queue ITSELF (confirm-before-ack so the
        // poison record is never lost) and then returns FALSE so the core's error action does NOT also forward —
        // returning true here would write the poison record to the error queue TWICE. So exactly one error-queue copy
        // exists, written by the adapter, with no loss. (The DLQ-configured path returns true and is pinned by
        // MustRepublishToDeadLetterPathThenAck above.)
        [Fact]
        public async Task MustRepublishToErrorPathThenAckAndReturnFalseWhenNoDeadLetterPathConfigured()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: null, errorQueuePath: ReceiverHarness.ErrorPath);
            await harness.PushAsync(deliveryTag: 12);
            var context = await harness.ReceiveAsync();

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            // (a) Exactly ONE error-queue copy: the adapter republishes-confirmed a single copy to the error path.
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.RoutingKey.Should().Be(ReceiverHarness.ErrorPath);
            // (b) The original is settled by an epoch-guarded ack of the carried delivery tag AFTER the confirmed
            // republish (NOT a requeue/republish-back), so it is not redelivered.
            harness.ConnectionSource.ReceiveChannel.Acks.Single().DeliveryTag.Should().Be(12UL);
            harness.ConnectionSource.ReceiveChannel.Nacks.Should().BeEmpty("the error-only deadletter acks; it does not requeue");
            // (c) Returns false so the core's ErrorQueueDispatcher is suppressed — the adapter already wrote the
            // single copy; letting the core forward again would be the duplicate.
            deadlettered.Should().BeFalse(
                "the method must return false so the core's error-recovery action is suppressed and the adapter's single error-queue copy is not duplicated");
        }

        // No message loss in the error-only path under a stale epoch: the republish is publisher-confirmed REGARDLESS
        // of epoch (a durable copy lands in the error queue), but the original ack is epoch-guarded exactly like every
        // other settlement — a recycled receive channel makes the ack a no-op so the broker redelivers the original.
        // The error queue therefore holds the durable copy and the redelivered original is the (absorbed) duplicate;
        // nothing is lost. The method returns false either way (the error-only path never returns true).
        [Fact]
        public async Task MustRepublishButNotAckErrorOnlyDeadletterWhenEpochStale()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: null, errorQueuePath: ReceiverHarness.ErrorPath);
            await harness.PushAsync(deliveryTag: 12);
            var context = await harness.ReceiveAsync();

            harness.ConnectionSource.AdvanceEpoch();

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            deadlettered.Should().BeFalse("the error-only deadletter returns false; under a stale epoch the ack is additionally a no-op (the false-ack guard)");
            // The durable error-queue copy is confirmed regardless of epoch — this is what prevents loss.
            harness.ConnectionSource.PublishChannels.Single().Publishes.Should().ContainSingle("the error-only deadletter republishes-confirmed a durable copy even under a stale epoch");
            // The ack is epoch-guarded: a recycled receive channel makes it a no-op so the broker redelivers.
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty();
        }

        // REPRODUCTION (r3407975400, refining r3407616994): when a receiver is registered with neither a dead-letter
        // nor an error path, a poison message that exhausts MaxReceiveAttempts has no valid deadletter destination.
        // DeadletterMessageAsync still throws on that misconfiguration as defense-in-depth, but the core
        // BrokeredMessageReceiver.TryDeadletterWithRecoveryAsync CATCHES any DeadletterMessageAsync exception and only
        // logs it — so the deadletter-time throw alone cannot stop the resulting redeliver-indefinitely hot loop. The
        // receiver therefore FAILS FAST AT INITIALIZATION (before StartReceivingAsync registers the AMQP consumer),
        // making the unconfigured-poison-target class unreachable: the receiver never begins consuming without a valid
        // poison destination. The startup error names the receiver queue so the misconfiguration is actionable.
        [Fact]
        public void MustFailFastAtInitializationWhenNeitherDeadLetterNorErrorPathConfigured()
        {
            var ex = ReceiverHarness.CaptureInitException(deadLetterQueuePath: null, errorQueuePath: null);

            ex.Should().BeOfType<InvalidOperationException>("the receiver must reject an unconfigured poison target before consumption starts, not after a poison message has already exhausted its retry budget")
              .Which.Message.Should().Contain(ReceiverHarness.ReceiverPath);
        }

        // The init-time guard accepts a receiver configured with only an error path (or only a dead-letter path):
        // either valid poison destination is sufficient, so initialization succeeds and the receiver consumes.
        [Fact]
        public void MustInitializeWhenOnlyErrorPathConfigured()
        {
            var ex = ReceiverHarness.CaptureInitException(deadLetterQueuePath: null, errorQueuePath: ReceiverHarness.ErrorPath);

            ex.Should().BeNull("a configured error path is a valid poison destination, so initialization must succeed");
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

        // --- TransactionMode.None is at-most-once via autoAck:true (ReceiveAndDelete equivalent) (r3408501700) ---

        // Under TransactionMode.None the consumer is registered with autoAck:true so the broker removes the delivery
        // at RECEIVE time (the AMQP ReceiveAndDelete equivalent the sibling ASB adapter uses), closing the crash/kill
        // window that manual-ack would leave open. Every other mode keeps manual ack (autoAck:false).
        [Fact]
        public void MustRegisterConsumerWithAutoAckUnderTransactionModeNone()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum, transactionMode: Chatter.MessageBrokers.Receiving.TransactionMode.None);

            harness.ConnectionSource.ReceiveChannel.LastConsumeAutoAck
                .Should().BeTrue("None is at-most-once: the broker must delete the delivery at receive (autoAck), not after the handler");
        }

        [Fact]
        public void MustRegisterConsumerWithManualAckWhenTransactionModeIsNotNone()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum, transactionMode: Chatter.MessageBrokers.Receiving.TransactionMode.ReceiveOnly);

            harness.ConnectionSource.ReceiveChannel.LastConsumeAutoAck
                .Should().BeFalse("only None auto-acks; every other mode keeps manual ack for the epoch-guarded settlement + retry/deadletter paths");
        }

        // Under None the delivery was already auto-acked (removed) at receive, so a handler-failure nack has nothing
        // to settle: it is a no-op — NOT a manual ack, NOT a requeue, NOT a republish. The message is already gone.
        [Fact]
        public async Task MustNoOpOnNackForQuorumWhenTransactionModeNone()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum, transactionMode: Chatter.MessageBrokers.Receiving.TransactionMode.None);
            await harness.PushAsync(deliveryTag: 5);
            var context = await harness.ReceiveAsync();
            var noneTx = new Chatter.MessageBrokers.Context.TransactionContext(ReceiverHarness.ReceiverPath, Chatter.MessageBrokers.Receiving.TransactionMode.None);

            var nacked = await harness.Receiver.NackMessageAsync(context, noneTx, CancellationToken.None);

            nacked.Should().BeFalse("under None the broker already removed the delivery at receive (autoAck); the nack is a no-op");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty("no manual delivery tag exists to ack under autoAck");
            harness.ConnectionSource.ReceiveChannel.Nacks.Should().BeEmpty("at-most-once must not requeue on failure");
        }

        // Under None a classic-queue nack likewise is a no-op: no manual ack and no republish-with-incremented-count
        // retry hop, because the delivery was auto-acked at receive.
        [Fact]
        public async Task MustNoOpOnNackForClassicWhenTransactionModeNone()
        {
            var harness = ReceiverHarness.Create(QueueType.Classic, transactionMode: Chatter.MessageBrokers.Receiving.TransactionMode.None);
            await harness.PushAsync(deliveryTag: 9);
            var context = await harness.ReceiveAsync();
            var noneTx = new Chatter.MessageBrokers.Context.TransactionContext(ReceiverHarness.ReceiverPath, Chatter.MessageBrokers.Receiving.TransactionMode.None);

            var nacked = await harness.Receiver.NackMessageAsync(context, noneTx, CancellationToken.None);

            nacked.Should().BeFalse("under None the delivery was auto-acked at receive; the classic nack is a no-op");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty("no manual delivery tag exists to ack under autoAck");
            harness.ConnectionSource.PublishChannels.Should().BeEmpty("at-most-once must not republish a retry copy");
        }

        // Under None a poison message is LOST, not deadlettered: deadletter is a no-op (no manual ack, no DLQ
        // republish) because the delivery was auto-acked at receive.
        [Fact]
        public async Task MustNoOpOnDeadletterWhenTransactionModeNone()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: ReceiverHarness.DeadLetterPath, transactionMode: Chatter.MessageBrokers.Receiving.TransactionMode.None);
            await harness.PushAsync(deliveryTag: 11);
            var context = await harness.ReceiveAsync();
            var noneTx = new Chatter.MessageBrokers.Context.TransactionContext(ReceiverHarness.ReceiverPath, Chatter.MessageBrokers.Receiving.TransactionMode.None);

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(context, noneTx, "poisoned", "bad", CancellationToken.None);

            deadlettered.Should().BeFalse("under None the delivery was auto-acked at receive; the deadletter is a no-op");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty("no manual delivery tag exists to ack under autoAck");
            harness.ConnectionSource.PublishChannels.Should().BeEmpty("at-most-once must not republish to the DLQ");
        }

        // Because None never deadletters, it has no poison target to require: the init-time fail-fast gate (which
        // rejects a receiver with neither a dead-letter nor an error path) must NOT fire under None.
        [Fact]
        public void MustNotRequirePoisonTargetAtInitializationWhenTransactionModeNone()
        {
            System.Exception captured = null;
            try
            {
                ReceiverHarness.Create(deadLetterQueuePath: null, errorQueuePath: null,
                    transactionMode: Chatter.MessageBrokers.Receiving.TransactionMode.None);
            }
            catch (System.Exception ex)
            {
                captured = ex;
            }

            captured.Should().BeNull("TransactionMode.None drops poison messages rather than deadlettering, so no poison destination is required");
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
