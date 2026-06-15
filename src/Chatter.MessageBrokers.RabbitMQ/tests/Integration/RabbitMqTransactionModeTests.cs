using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Integration proof that the RabbitMQ adapter honors core behavior C9 (TransactionMode) — the at-least-once
    // vs at-most-once delivery divergence — against a REAL broker. RabbitMqReceiver sets _autoAck =
    // (TransactionMode == None): with TransactionMode.None the delivery is acked the moment it is pulled
    // (at-most-once), so a throwing handler loses the message with NO redelivery; with TransactionMode.ReceiveOnly
    // the delivery stays unacked until the handler succeeds, so a throwing handler nacks/republishes and the
    // broker REDELIVERS (at-least-once), climbing ReceiveAttempts. Asserts at the Chatter level — the handler's
    // InvocationCount and the MessageContext.ReceiveAttempts stamp — never against raw RabbitMQ.Client.
    //
    // ANTI-INFINITE-LOOP (ReceiveOnly case): ThrowOnHandle is flipped to null once >= 2 invocations are observed
    // so the message finally acks before DisposeAsync drains the pump. MaxReceiveAttempts is left at the default
    // (10), well above the 2 redeliveries the test drives, so the message nacks/redelivers (never deadletters)
    // in the window under test.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqTransactionModeTests
    {
        // Long enough that the broker redelivers the ReceiveOnly-mode nack within the window.
        private static readonly TimeSpan RedeliveryWait = TimeSpan.FromSeconds(30);

        // Bounded window in which the None-mode (at-most-once) message must NOT reappear. After the single
        // delivery is acked-on-pull and the handler throws, no redelivery can occur — the absence is asserted by
        // waiting this long and confirming the invocation count never climbed past 1. Generous enough that a
        // genuine broker redelivery would have landed within it, so a still-1 count is a true negative.
        private static readonly TimeSpan NoRedeliveryWait = TimeSpan.FromSeconds(10);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqTransactionModeTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // Distinct command type so the ReceiveOnly scenario's queue state is independent of the None scenario and
        // of every other integration test in the collection.
        public sealed class ReceiveOnlyTxCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // Distinct command type for the None (at-most-once) scenario so its queue state is independent.
        public sealed class NoneTxCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // C9 at-least-once: TransactionMode.ReceiveOnly keeps the delivery unacked until the handler succeeds, so a
        // throwing handler causes a nack/republish and the broker REDELIVERS. Assert the handler is invoked >= 2
        // times (at least one redelivery) and that ReceiveAttempts climbs (>= 2) across deliveries.
        [RequiresDockerFact]
        public async Task ReceiveOnlyTransactionModeRedeliversOnThrow()
        {
            var set = RabbitMqTopology.CreateSet("txmode_receiveonly", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<ReceiveOnlyTxCommand>(
                    set.WorkQueueName,
                    transactionMode: TransactionMode.ReceiveOnly,
                    deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(ReceiveOnlyTxCommand));
            try
            {
                await harness.StartAsync();

                // Arm the throw BEFORE sending so the handler throws on the very first delivery. ThrowOnHandle is
                // Func<Exception>: a fresh instance per invocation matches the RecordingMessageHandler<T> contract.
                harness.GetSignal<ReceiveOnlyTxCommand>().ThrowOnHandle =
                    () => new InvalidOperationException("transaction-mode receiveonly forced throw");

                await harness.SendToQueueAsync(new ReceiveOnlyTxCommand { Marker = "txmode-receiveonly" }, set.WorkQueueName);

                // Wait until at least 2 handler invocations have been observed (first delivery + at least one
                // redelivery). WaitForInvocationCountAsync returns the last observed count, which may be below
                // minCount when the timeout elapses — the assertion below catches that case explicitly.
                var observedCount = await harness.WaitForInvocationCountAsync<ReceiveOnlyTxCommand>(
                    minCount: 2, RedeliveryWait);

                // ANTI-INFINITE-LOOP: stop throwing so the message is acked on the next receive before DisposeAsync
                // drains the pump.
                harness.GetSignal<ReceiveOnlyTxCommand>().ThrowOnHandle = null;

                observedCount.Should().BeGreaterThanOrEqualTo(2,
                    "TransactionMode.ReceiveOnly keeps the delivery unacked until the handler succeeds, so the " +
                    "throwing handler nacks/republishes and the broker redelivers — the handler must be invoked at " +
                    "least twice: the original delivery plus at least one redelivery (at-least-once)");

                // ReceiveAttempts must climb: on a quorum queue attempts = native x-delivery-count + 1, which the
                // broker advances on each redelivery. Capture the attempt stamp from each recorded invocation and
                // assert the maximum observed value exceeds 1.
                var records = harness.GetSignal<ReceiveOnlyTxCommand>().Records.ToList();
                var maxAttempts = records
                    .Where(r => r.Context?.BrokeredMessage?.MessageContext?.ContainsKey(MessageContext.ReceiveAttempts) == true)
                    .Select(r => Convert.ToInt32(r.Context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts]))
                    .DefaultIfEmpty(0)
                    .Max();

                maxAttempts.Should().BeGreaterThan(1,
                    "the quorum native x-delivery-count must advance on each redelivery, so ReceiveAttempts " +
                    "(x-delivery-count + 1) must exceed 1 after at least one ReceiveOnly-mode redelivery");
            }
            finally
            {
                harness.GetSignal<ReceiveOnlyTxCommand>().ThrowOnHandle = null;
                await harness.DisposeAsync();
            }
        }

        // C9 at-most-once: TransactionMode.None acks the delivery the moment it is pulled (RabbitMqReceiver sets
        // _autoAck = (TransactionMode == None)), so a throwing handler LOSES the message with NO redelivery. Assert
        // the handler is invoked EXACTLY once and that no further delivery arrives within a bounded wait window
        // (the message was acked-on-delivery and dropped).
        [RequiresDockerFact]
        public async Task NoneTransactionModeDropsMessageOnThrowWithNoRedelivery()
        {
            var set = RabbitMqTopology.CreateSet("txmode_none", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<NoneTxCommand>(
                    set.WorkQueueName,
                    transactionMode: TransactionMode.None,
                    deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(NoneTxCommand));
            try
            {
                await harness.StartAsync();

                // Throw on every delivery. Under TransactionMode.None the delivery is already acked when the handler
                // runs, so the throw cannot trigger a nack/redelivery — it just surfaces as a faulted handle that
                // the receiver loop swallows after the message is already gone. ThrowOnHandle stays armed for the
                // whole window so that, were a redelivery to (wrongly) occur, the second invocation would still be
                // recorded and climb the count past 1.
                harness.GetSignal<NoneTxCommand>().ThrowOnHandle =
                    () => new InvalidOperationException("transaction-mode none forced throw");

                await harness.SendToQueueAsync(new NoneTxCommand { Marker = "txmode-none" }, set.WorkQueueName);

                // First, wait until the single delivery has actually been handled (so the assertion below is not a
                // vacuous "0 == 1 not yet delivered"). The handler IS invoked exactly once on the original delivery.
                var firstInvocation = await harness.WaitForHandledAsync<NoneTxCommand>(RedeliveryWait);
                firstInvocation.Should().NotBeNull();

                // Then assert NON-OCCURRENCE the same way the suite proves a negative: poll for a SECOND invocation
                // across the bounded no-redelivery window and confirm the count never reaches 2. Because the message
                // was acked-on-pull and dropped on throw, no redelivery can land, so the count stays pinned at 1.
                var observedCount = await harness.WaitForInvocationCountAsync<NoneTxCommand>(
                    minCount: 2, NoRedeliveryWait);

                observedCount.Should().Be(1,
                    "TransactionMode.None acks the delivery the moment it is pulled (at-most-once), so the throwing " +
                    "handler drops the message — it must be invoked EXACTLY once with no redelivery within the " +
                    "bounded wait window");

                // Corroborate from the recorded invocations: exactly one delivery was ever captured.
                var records = harness.GetSignal<NoneTxCommand>().Records.ToList();
                records.Count.Should().Be(1,
                    "an at-most-once (acked-on-pull) delivery that throws is lost, so the recorder must hold exactly " +
                    "one invocation and never a redelivered second copy");
            }
            finally
            {
                harness.GetSignal<NoneTxCommand>().ThrowOnHandle = null;
                await harness.DisposeAsync();
            }
        }
    }
}
