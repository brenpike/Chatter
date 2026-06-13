using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Recovery-epoch false-ack proof for the RabbitMQ adapter (R-STEP-003). The DEFECT: the receive channel rides
    // an AutomaticRecoveryEnabled connection, so RabbitMQ.Client transparently recovers the SAME IChannel on a
    // reconnect. EnsureReceiveChannelAsync then early-returns without recreating the channel, so absent the
    // RecoverySucceededAsync subscription the receive-channel epoch stays stale — and an in-flight pre-recovery
    // delivery tag (now INVALID on the recovered session) would pass the stale-epoch settle guard and be
    // false-acked, swallowing the message even though the broker never saw the ack.
    //
    // The SYSTEM UNDER TEST is the live Chatter receive path against a real broker. Scenario:
    //   1. A command is delivered; the handler BLOCKS, holding the delivery in-flight (unacked) on the receive
    //      channel and pinning the pre-recovery epoch on the buffered ReceivedMessage.
    //   2. The test forcibly drops the broker connection (management API), so the client auto-recovers the SAME
    //      receive channel. The source's RecoverySucceededAsync handler advances the epoch.
    //   3. The handler unblocks; Chatter attempts to ack the original delivery tag. Because the tag's epoch is now
    //      stale, the settle is a NO-OP — no ack reaches the (recovered) broker session.
    //   4. The broker, having requeued the unacked delivery when the connection dropped, REDELIVERS the message,
    //      so the handler is invoked AGAIN. This second delivery does not block and acks normally.
    // The proof the epoch advanced and the false-ack was prevented is the redelivery: invocation count >= 2. Were
    // the epoch left stale (the defect), the stale ack would have settled the original tag, the broker would have
    // nothing to redeliver, and the handler would be invoked exactly once.
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
        public async Task PostRecoverySettlementIsNoOpSoBrokerRedelivers()
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
                        // Redelivery: do not block, let it ack normally so the message finally settles.
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

                // Force automatic recovery: drop the broker connection out-of-band. The client recovers the SAME
                // receive channel, and the source's RecoverySucceededAsync handler advances the epoch.
                var killed = await RabbitMqConnectionKiller.KillAllConnectionsAsync(amqpUri, CancellationToken.None);
                killed.Should().BeGreaterThan(0,
                    "at least the receiver's broker connection must be dropped to trigger automatic recovery");

                // Give the client a moment to complete recovery (epoch advance) before the blocked handler's ack
                // runs, so the ack is evaluated against the post-recovery epoch.
                await Task.Delay(TimeSpan.FromSeconds(5));

                // Release the first delivery: Chatter now attempts to ack the original (pre-recovery) delivery tag.
                // The stale-epoch guard makes that ack a no-op, so the broker still has the message and redelivers.
                recoveryForced.Release();

                var observedCount = await harness.WaitForInvocationCountAsync<RecoveryEpochCommand>(
                    minCount: 2, RedeliveryWait);

                observedCount.Should().BeGreaterThanOrEqualTo(2,
                    "the post-recovery settlement of the pre-recovery delivery tag must be a no-op (stale epoch), so " +
                    "the broker redelivers the requeued message and the handler is invoked a second time; were the " +
                    "epoch left stale on recovery the original ack would have settled the message and there would be " +
                    "no redelivery");
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
