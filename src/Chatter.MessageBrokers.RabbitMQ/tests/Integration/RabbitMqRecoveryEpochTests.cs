using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Recovery-epoch BOTH-HALVES proof for the RabbitMQ adapter (E-STEP-006). The receive-channel epoch lifecycle
    // is closed-by-construction (commit redesigning RabbitMqConnectionSource): the source runs with
    // AutomaticRecoveryEnabled but TopologyRecoveryEnabled=false, and on every RecoverySucceededAsync it recreates
    // the receive IChannel, bumps the epoch, and re-registers the consumer UNDER THE GATE as one atomic event. So a
    // delivery's stamped epoch always equals the session that delivered it — pre-recovery deliveries carry the old
    // epoch, post-recovery deliveries carry the new one.
    //
    // This test asserts BOTH halves of the invariant the design guarantees:
    //   HALF 1 (false-ack is impossible): a pre-recovery in-flight delivery whose tag is meaningless on the
    //     recovered session is NOT false-acked — the stale-epoch guard no-ops the settle and the broker redelivers
    //     the requeued message (invocation count reaches >= 2).
    //   HALF 2 (post-recovery settlement is NOT a no-op): the redelivered (post-recovery) delivery ACTUALLY
    //     SETTLES — its tag carries the post-recovery epoch, so the ack reaches the broker, the message leaves the
    //     queue, and the redelivery count CONVERGES (stops growing). Were post-recovery settlement still a no-op
    //     (the superseded bump-only design's EDGE 2), the redelivered message would be redelivered again and again
    //     in an unbounded duplicate loop, so the invocation count would keep climbing past 2.
    //
    // The SYSTEM UNDER TEST is the live Chatter receive path against a real broker. Scenario:
    //   1. A command is delivered; the handler BLOCKS on the FIRST invocation, holding the delivery in-flight
    //      (unacked) on the receive channel and pinning the pre-recovery epoch on the buffered ReceivedMessage.
    //   2. The test forcibly drops the broker connection (management API), so the client auto-recovers and the
    //      source's RecoverySucceededAsync handler recreates the channel + re-registers the consumer + bumps the
    //      epoch atomically.
    //   3. The handler unblocks; Chatter attempts to ack the original delivery tag. Its epoch is now stale, so the
    //      settle is a NO-OP — no ack reaches the recovered session (HALF 1).
    //   4. The broker, having requeued the unacked delivery when the connection dropped, REDELIVERS the message on
    //      the recovered (re-registered) consumer. That redelivery does NOT block and acks normally; because it
    //      carries the post-recovery epoch the ack settles and the message leaves the queue — so the count
    //      converges and does not loop (HALF 2).
    //
    // Gated by [RequiresDockerFact] + Category=Integration: SKIPPED (never failed) when Docker is absent, so a
    // plain `dotnet test` stays green; the nightly RabbitMQ CI lane runs it for real. Mirrors the
    // RabbitMqNackRedeliveryTests fixture/collection/harness shape.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqRecoveryEpochTests
    {
        // Generous: forcing a connection drop, the client's automatic recovery backoff, and the broker's
        // requeue+redelivery all happen serially before the second invocation is observed.
        private static readonly TimeSpan RedeliveryWait = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan FirstDeliveryWait = TimeSpan.FromSeconds(30);

        // After the post-recovery redelivery settles, watch the invocation count for a quiet window. If
        // post-recovery settlement were a no-op the message would be redelivered repeatedly, so the count would
        // keep climbing across this window; a stable count proves the loop is closed and the message settled.
        private static readonly TimeSpan ConvergenceWindow = TimeSpan.FromSeconds(20);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqRecoveryEpochTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // Distinct command type so this scenario's queue state is independent of the other integration tests in
        // the collection.
        public sealed class RecoveryEpochCommand : ICommand
        {
            public string Marker { get; set; }
        }

        [RequiresDockerFact]
        public async Task PreRecoveryAckNoOpsAndPostRecoveryDeliverySettlesAndConverges()
        {
            var amqpUri = _fixture.GetAmqpConnectionString();

            // Quorum queue: an unacked delivery whose channel/connection drops is requeued by the broker and
            // redelivered (with the native x-delivery-count advanced), so the redelivery is observable.
            var set = RabbitMqTopology.CreateSet("recovery_epoch", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(amqpUri, set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                amqpUri,
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<RecoveryEpochCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(RecoveryEpochCommand));

            // Released once the test has forced recovery, so the FIRST delivery's handler unblocks and Chatter
            // runs the (now stale) ack. Only the first invocation blocks; later redeliveries return immediately.
            using var recoveryForced = new SemaphoreSlim(0, 1);
            var firstDeliveryObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var signal = harness.GetSignal<RecoveryEpochCommand>();
                signal.BlockOnHandle = async invocationCount =>
                {
                    if (invocationCount > 1)
                    {
                        // Post-recovery redelivery: do not block, let it ack normally so the message finally
                        // settles. This is the delivery whose successful settlement HALF 2 asserts.
                        return;
                    }

                    firstDeliveryObserved.TrySetResult();
                    // Hold the original delivery in-flight until the test has forced recovery.
                    await recoveryForced.WaitAsync().ConfigureAwait(false);
                };

                await harness.StartAsync();
                await harness.SendToQueueAsync(new RecoveryEpochCommand { Marker = "recovery-epoch" }, set.WorkQueueName);

                // Wait until the first delivery is in-flight (handler entered and is blocking).
                var firstDelivery = await Task.WhenAny(firstDeliveryObserved.Task, Task.Delay(FirstDeliveryWait));
                firstDelivery.Should().Be(firstDeliveryObserved.Task,
                    "the handler must receive the first delivery and block before the connection is dropped");

                // Force automatic recovery: drop the broker connection out-of-band. The source's
                // RecoverySucceededAsync handler recreates the receive channel, bumps the epoch, and re-registers
                // the consumer under the gate.
                var killed = await RabbitMqConnectionKiller.KillAllConnectionsAsync(
                    amqpUri, _fixture.GetManagementBaseUri(), CancellationToken.None);
                killed.Should().BeGreaterThan(0,
                    "at least the receiver's broker connection must be dropped to trigger automatic recovery");

                // Give the client a moment to complete recovery (channel recreate + epoch bump + consumer
                // re-registration) before the blocked handler's ack runs, so the ack is evaluated against the
                // post-recovery epoch.
                await Task.Delay(TimeSpan.FromSeconds(5));

                // Release the first delivery: Chatter now attempts to ack the original (pre-recovery) delivery tag.
                // The stale-epoch guard makes that ack a no-op, so the broker still has the message and redelivers.
                recoveryForced.Release();

                // HALF 1: the pre-recovery ack no-ops, so the broker redelivers and the handler is invoked again.
                var observedCount = await harness.WaitForInvocationCountAsync<RecoveryEpochCommand>(
                    minCount: 2, RedeliveryWait);

                observedCount.Should().BeGreaterThanOrEqualTo(2,
                    "the post-recovery settlement of the PRE-recovery delivery tag must be a no-op (stale epoch), so " +
                    "the broker redelivers the requeued message and the handler is invoked a second time; were the " +
                    "epoch not advanced on recovery the original ack would have settled the message and there would " +
                    "be no redelivery");

                // HALF 2: the post-recovery redelivery carries the post-recovery epoch, so its ack settles and the
                // message leaves the queue. Prove the duplicate loop is GONE by showing the invocation count
                // CONVERGES — it stops growing across a quiet window. A no-op post-recovery settle (the superseded
                // bump-only design's EDGE 2) would keep redelivering, so the count would keep climbing here.
                var countAfterFirstRedelivery = harness.GetSignal<RecoveryEpochCommand>().InvocationCount;
                await Task.Delay(ConvergenceWindow);
                var countAfterConvergenceWindow = harness.GetSignal<RecoveryEpochCommand>().InvocationCount;

                countAfterConvergenceWindow.Should().Be(countAfterFirstRedelivery,
                    "the post-recovery redelivery must ACTUALLY SETTLE (its tag carries the post-recovery epoch), so " +
                    "the message leaves the queue and the redelivery count CONVERGES; were post-recovery settlement " +
                    "still a no-op the broker would redeliver the same message unboundedly and the invocation count " +
                    "would keep climbing across the convergence window (the EDGE-2 duplicate loop)");
            }
            finally
            {
                // Ensure the blocked handler is released even on an assertion failure so DisposeAsync can drain.
                if (recoveryForced.CurrentCount == 0)
                {
                    recoveryForced.Release();
                }

                harness.GetSignal<RecoveryEpochCommand>().BlockOnHandle = null;
                await harness.DisposeAsync();
            }
        }
    }
}
