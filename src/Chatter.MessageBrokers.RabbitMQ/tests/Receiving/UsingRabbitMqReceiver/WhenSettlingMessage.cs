using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.Receiving;
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
    // Pins RabbitMqReceiver settlement against the three-valued settlement contract of
    // IMessagingInfrastructureReceiver (ADR-0010 D7: every in-repo implementation is contract-tested against all
    // three outcomes): ack on a matching epoch acks the carried delivery tag and reports Settled; nack requeues
    // (Quorum) or republishes-with-incremented-count-then-acks (Classic) and reports Settled; deadletter
    // republishes-confirmed to the attribute-declared deadletter/error path then acks and reports Settled on BOTH
    // paths. A settlement with nothing to settle (TransactionMode.None at-most-once) reports NotRequired; a
    // settlement that was attempted and did not happen (no carried delivery, or a stale channel epoch) reports
    // Failed. EVERY settlement is epoch-guarded — a settlement carrying a stale epoch (forced via the in-memory
    // source's AdvanceEpoch) makes the ACK a no-op (the false-ack guard) while the republish is publisher-confirmed
    // regardless, so the durable copy survives and the broker redelivers the original.
    // The republish-then-ack ordering is asserted: the confirmed republish is recorded BEFORE the original ack.
    // The ERROR-QUEUE control signal is asserted SEPARATELY from the outcome, via WritesToErrorQueue — see
    // MustSuppressTheCoresErrorRecoveryActionOnTheErrorOnlyConfig.
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

            acked.Outcome.Should().Be(SettlementOutcome.Settled);
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

            // A stale epoch is a settlement that was ATTEMPTED and did not happen — Failed, not NotRequired.
            // The broker will redeliver; the receiver reports the failed receive and does not retry the settlement.
            acked.Outcome.Should().Be(SettlementOutcome.Failed, "a settlement carrying a stale epoch was attempted and did not happen (the false-ack guard)");
            acked.Reason.Should().NotBeNullOrWhiteSpace("a settlement that did not happen must explain itself");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReportFailedOnAckWhenContextCarriesNoReceivedMessage()
        {
            var harness = ReceiverHarness.Create();
            var bareContext = new Chatter.MessageBrokers.Context.MessageBrokerContext(
                "id", new byte[] { 1 }, new Dictionary<string, object>(), ReceiverHarness.ReceiverPath, CancellationToken.None, new RabbitMqBodyConverter());

            var acked = await harness.Receiver.AckMessageAsync(bareContext, transactionContext: null, CancellationToken.None);

            acked.Outcome.Should().Be(SettlementOutcome.Failed, "an acknowledgement that could not locate its delivery was attempted and did not happen");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty();
        }

        // A bare context carrying no ReceivedMessage (no Container.Include) has nothing to settle: nack must return
        // false and perform no nack/republish — mirroring the ack false arm above.
        [Fact]
        public async Task MustReportFailedOnNackWhenContextCarriesNoReceivedMessage()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            var bareContext = new Chatter.MessageBrokers.Context.MessageBrokerContext(
                "id", new byte[] { 1 }, new Dictionary<string, object>(), ReceiverHarness.ReceiverPath, CancellationToken.None, new RabbitMqBodyConverter());

            var nacked = await harness.Receiver.NackMessageAsync(bareContext, transactionContext: null, CancellationToken.None);

            nacked.Outcome.Should().Be(SettlementOutcome.Failed, "a negative acknowledgement that could not locate its delivery was attempted and did not happen");
            harness.ConnectionSource.ReceiveChannel.Nacks.Should().BeEmpty("no carried delivery means nothing to nack");
            harness.ConnectionSource.PublishChannels.Should().BeEmpty("no carried delivery means nothing to republish");
        }

        // A bare context carrying no ReceivedMessage has nothing to deadletter: deadletter must return false and
        // perform no publish/ack.
        [Fact]
        public async Task MustReportFailedOnDeadletterWhenContextCarriesNoReceivedMessage()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: ReceiverHarness.DeadLetterPath);
            var bareContext = new Chatter.MessageBrokers.Context.MessageBrokerContext(
                "id", new byte[] { 1 }, new Dictionary<string, object>(), ReceiverHarness.ReceiverPath, CancellationToken.None, new RabbitMqBodyConverter());

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                bareContext, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            deadlettered.Outcome.Should().Be(SettlementOutcome.Failed, "a deadletter that could not locate its delivery was attempted and did not happen");
            harness.ConnectionSource.PublishChannels.Should().BeEmpty("no carried delivery means nothing to republish");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty("no carried delivery means nothing to ack");
        }

        // --- nack: Quorum requeues natively ---

        [Fact]
        public async Task MustRequeueOnNackForQuorum()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum);
            await harness.PushAsync(deliveryTag: 5);
            var context = await harness.ReceiveAsync();

            var nacked = await harness.Receiver.NackMessageAsync(context, transactionContext: null, CancellationToken.None);

            nacked.Outcome.Should().Be(SettlementOutcome.Settled);
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

            nacked.Outcome.Should().Be(SettlementOutcome.Failed);
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

            nacked.Outcome.Should().Be(SettlementOutcome.Settled);
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
            harness.ConnectionSource.PublishChannels.Single().Publishes.Single().Seq
                .Should().BeLessThan(harness.ConnectionSource.ReceiveChannel.Acks.Single().Seq,
                    "the confirmed republish must be recorded before the original ack (ADR-0001 confirm-before-ack)");
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
            nacked.Outcome.Should().Be(SettlementOutcome.Failed);
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

            deadlettered.Outcome.Should().Be(SettlementOutcome.Settled);
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.RoutingKey.Should().Be(ReceiverHarness.DeadLetterPath);
            republish.Headers[MessageContext.FailureDetails].Should().Be("poisoned");
            republish.Headers[MessageContext.FailureDescription].Should().Be("could not be handled");
            harness.ConnectionSource.ReceiveChannel.Acks.Single().DeliveryTag.Should().Be(11UL);
            harness.ConnectionSource.PublishChannels.Single().Publishes.Single().Seq
                .Should().BeLessThan(harness.ConnectionSource.ReceiveChannel.Acks.Single().Seq,
                    "the confirmed deadletter republish must be recorded before the original ack (ADR-0001 confirm-before-ack)");
        }

        // OWNERSHIP (r3408649034): the dead-letter queue is the ADAPTER's responsibility; the ERROR queue is the
        // CORE's. On max-receives the core runs deadletter FIRST and, ONLY when the deadletter SETTLED and the
        // infrastructure does NOT write to the error queue itself, ALSO runs its error-recovery action
        // (ErrorQueueDispatcher → IForwardMessages) which forwards the inbound message to the error queue via this
        // same RabbitMQ infrastructure. For an ERROR-ONLY config (no dead-letter queue) the adapter
        // republishes-confirmed the single durable copy to the error queue ITSELF (confirm-before-ack so the poison
        // record is never lost) and reports Settled — because it DID settle — while declaring WritesToErrorQueue so
        // the core's error action does not also forward. So exactly one error-queue copy exists, written by the
        // adapter, with no loss. (The DLQ-configured path is pinned by MustRepublishToDeadLetterPathThenAck above.)
        [Fact]
        public async Task MustRepublishToErrorPathThenAckWhenNoDeadLetterPathConfigured()
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
            // (c) The outcome is TRUTHFUL: the delivery WAS settled. Suppressing the core's error action is a
            // separate control signal (WritesToErrorQueue), never a misreported settlement outcome.
            deadlettered.Outcome.Should().Be(SettlementOutcome.Settled,
                "the error-only deadletter republished a durable copy and acked the original — it settled the delivery");
            // (d) The confirmed error-path republish is recorded BEFORE the original ack.
            harness.ConnectionSource.PublishChannels.Single().Publishes.Single().Seq
                .Should().BeLessThan(harness.ConnectionSource.ReceiveChannel.Acks.Single().Seq,
                    "the confirmed error-path republish must be recorded before the original ack (ADR-0001 confirm-before-ack)");
        }

        // --- the error-queue control signal, split off the settlement outcome ---

        // REGRESSION PIN — THE DOUBLE-WRITE (adapter half). The error-only config must produce EXACTLY ONE durable
        // copy in the Error Queue: the adapter writes that copy itself, so Error-Queue Write Ownership must report
        // `WritesToErrorQueue == true` here, which is what tells the Brokered Message Receiver's error-recovery
        // action to stay off this delivery and upholds the single-copy rule.
        // THESE tests pin only the ADAPTER half of that contract — that `WritesToErrorQueue` has the correct
        // polarity for each configuration (asserted below and at line ~299). The CORE gate —
        //     deadletterResult.IsSettled && !_infrastructureReceiver.WritesToErrorQueue
        // (BrokeredMessageReceiver.cs:916) — is pinned separately, in the module that owns it, by
        // `MustSuppressMaxReceivesActionWhenInfrastructureOwnsTheErrorQueueWrite` in Chatter.MessageBrokers.Tests.
        // That test is where the two halves of the contract actually meet. Nothing in this file can detect a
        // change to the real core predicate — the `coreRunsItsErrorRecoveryAction` locals below recompute the
        // same expression locally and only DOCUMENT its shape; see the note on each.
        [Fact]
        public async Task MustSuppressTheCoresErrorRecoveryActionOnTheErrorOnlyConfig()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: null, errorQueuePath: ReceiverHarness.ErrorPath);
            await harness.PushAsync(deliveryTag: 12);
            var context = await harness.ReceiveAsync();

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            harness.ConnectionSource.PublishChannels.Single().Publishes.Should().ContainSingle(
                "the adapter writes exactly one durable copy to the Error Queue");
            harness.Receiver.WritesToErrorQueue.Should().BeTrue(
                "the error-only config makes the adapter the owner of the Error Queue write");

            // Documentation-only: recomputes the core's gating expression locally to show its expected shape for
            // this configuration. It CANNOT detect a change to the real predicate in BrokeredMessageReceiver — see
            // the comment above this test's [Fact].
            var coreRunsItsErrorRecoveryAction = deadlettered.IsSettled && !harness.Receiver.WritesToErrorQueue;
            coreRunsItsErrorRecoveryAction.Should().BeFalse(
                "the core must not write a second copy of the poison message to the Error Queue the adapter already wrote");
        }

        // The DLQ-configured path keeps the core as the Error Queue owner: the adapter republished to the DEAD-LETTER
        // queue, not the error queue, so the core's error-recovery action must still run and forward its copy.
        [Fact]
        public async Task MustLeaveTheCoresErrorRecoveryActionToRunWhenADeadLetterPathIsConfigured()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: ReceiverHarness.DeadLetterPath);
            await harness.PushAsync(deliveryTag: 11);
            var context = await harness.ReceiveAsync();

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            harness.Receiver.WritesToErrorQueue.Should().BeFalse(
                "with a dead-letter queue configured the adapter never writes to the Error Queue, so the core owns that write");

            // Documentation-only: recomputes the core's gating expression locally to show its expected shape for
            // this configuration. It CANNOT detect a change to the real predicate in BrokeredMessageReceiver —
            // see the comment above MustSuppressTheCoresErrorRecoveryActionOnTheErrorOnlyConfig.
            var coreRunsItsErrorRecoveryAction = deadlettered.IsSettled && !harness.Receiver.WritesToErrorQueue;
            coreRunsItsErrorRecoveryAction.Should().BeTrue(
                "the core must still forward its error-queue copy when the adapter deadlettered to the dead-letter queue");
        }

        // No message loss in the error-only path under a stale epoch: the republish is publisher-confirmed REGARDLESS
        // of epoch (a durable copy lands in the error queue), but the original ack is epoch-guarded exactly like every
        // other settlement — a recycled receive channel makes the ack a no-op so the broker redelivers the original.
        // The error queue therefore holds the durable copy and the redelivered original is the (absorbed) duplicate;
        // nothing is lost. The outcome is Failed — the ack was attempted and did not happen — which ALSO keeps the
        // core's error-recovery action off this path, exactly as before.
        [Fact]
        public async Task MustRepublishButNotAckErrorOnlyDeadletterWhenEpochStale()
        {
            var harness = ReceiverHarness.Create(deadLetterQueuePath: null, errorQueuePath: ReceiverHarness.ErrorPath);
            await harness.PushAsync(deliveryTag: 12);
            var context = await harness.ReceiveAsync();

            harness.ConnectionSource.AdvanceEpoch();

            var deadlettered = await harness.Receiver.DeadletterMessageAsync(
                context, transactionContext: null, "poisoned", "bad", CancellationToken.None);

            deadlettered.Outcome.Should().Be(SettlementOutcome.Failed, "under a stale epoch the ack was attempted and did not happen (the false-ack guard)");
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

            deadlettered.Outcome.Should().Be(SettlementOutcome.Failed);
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

        // Under None the delivery was already auto-acked (removed) at receive, so there is nothing left to
        // acknowledge. That is NotRequired, NOT Failed: the at-most-once contract is already satisfied by the
        // auto-ack, so no settlement is owed and none may be reported as missing.
        [Fact]
        public async Task MustReportNotRequiredOnAckWhenTransactionModeNone()
        {
            var harness = ReceiverHarness.Create(QueueType.Quorum, transactionMode: Chatter.MessageBrokers.Receiving.TransactionMode.None);
            await harness.PushAsync(deliveryTag: 77);
            var context = await harness.ReceiveAsync();
            var noneTx = new Chatter.MessageBrokers.Context.TransactionContext(ReceiverHarness.ReceiverPath, Chatter.MessageBrokers.Receiving.TransactionMode.None);

            var acked = await harness.Receiver.AckMessageAsync(context, noneTx, CancellationToken.None);

            acked.Outcome.Should().Be(SettlementOutcome.NotRequired, "the at-most-once contract is already satisfied by the auto-ack");
            acked.Reason.Should().NotBeNullOrWhiteSpace("an unsettled outcome must explain itself");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty("no manual delivery tag exists to ack under autoAck");
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

            nacked.Outcome.Should().Be(SettlementOutcome.NotRequired, "under None the broker already removed the delivery at receive (autoAck); there is nothing to settle");
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

            nacked.Outcome.Should().Be(SettlementOutcome.NotRequired, "under None the delivery was auto-acked at receive; there is nothing to settle");
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

            deadlettered.Outcome.Should().Be(SettlementOutcome.NotRequired, "under None the delivery was auto-acked at receive; there is nothing to settle");
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

            nacked.Outcome.Should().Be(SettlementOutcome.Settled);
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

            deadlettered.Outcome.Should().Be(SettlementOutcome.Settled);
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

            nacked.Outcome.Should().Be(SettlementOutcome.Settled);
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

            nacked.Outcome.Should().Be(SettlementOutcome.Settled);
            var republish = harness.ConnectionSource.PublishChannels.Single().Publishes.Single();
            republish.ContentType.Should().Be("application/json", "the carried native ContentType is the authoritative frame value on republish");
            republish.CorrelationId.Should().Be("corr-123", "the carried native CorrelationId is the authoritative frame value on republish");
        }
    }
}
