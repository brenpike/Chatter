using Chatter.MessageBrokers.Context;
using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver
{
    // Broker-free proof of the TERMINAL, SURGICAL receiver teardown lifecycle (closes PR #194 r3407966808; ADR-0005).
    // RabbitMqReceiver.StopReceiver was previously buffer-completion ONLY: the AMQP consumer was never cancelled and
    // the receive channel never torn down, so the source kept the registered consumer alive and prefetched deliveries
    // could keep being pushed into a completed buffer. The fix routes teardown through the connection source, which
    // OWNS the receive channel + consumer lifecycle (ADR-0002/0003): StopReceiver cancels the consumer FIRST, then
    // completes the buffer; the source's StopReceivingAsync cancels + tears down the RECEIVE CHANNEL ONLY, never the
    // connection or publish pool (the singleton source is shared with the sender). These tests pin that contract
    // through the real receiver over the in-memory source + RecordingChannel, deterministically, with no broker.
    public class WhenStoppingReceiver : Testing.Core.Context
    {
        // StopReceiver cancels the registered consumer BEFORE completing the buffer: the in-memory source records the
        // cancelled tag (the tag BasicConsumeAsync returned) on the receive channel that delivered, proving the
        // consumer is cancelled rather than the buffer merely completed.
        [Fact]
        public async Task MustCancelConsumerBeforeCompletingBuffer()
        {
            var harness = ReceiverHarness.Create();
            var receiveChannel = harness.ConnectionSource.ReceiveChannel;

            await harness.Receiver.StopReceiver();

            receiveChannel.CancelledConsumerTags.Should().ContainSingle()
                .Which.Should().Be("in-memory-consumer-tag",
                    "StopReceiver must cancel the AMQP consumer that BasicConsumeAsync registered");
            receiveChannel.Disposed.Should().BeTrue("StopReceiver must tear down the receive channel");
            harness.ConnectionSource.ReceivingStopped.Should().BeTrue("the source must record the terminal stop");
        }

        // SURGICAL: StopReceiver must NOT dispose the connection or the publish pool — the singleton source is shared
        // with the sender, which keeps publishing after the receiver stops. Prove a publish still succeeds (a rental
        // is still handed out) after StopReceiver.
        [Fact]
        public async Task MustNotTearDownPublishPathOnStop()
        {
            var harness = ReceiverHarness.Create();

            await harness.Receiver.StopReceiver();

            await using var rental = await harness.ConnectionSource.AcquirePublishChannelAsync(CancellationToken.None);
            rental.Should().NotBeNull("the sender's publish path must still work after the receiver stops");
            rental.Channel.Should().NotBeNull("a publish channel must still be acquirable after StopReceiver");
        }

        // A delivery the broker might still try to push AFTER StopReceiver is safely DROPPED (the consumer is
        // cancelled and the channel torn down) rather than thrown into the completed buffer writer.
        [Fact]
        public async Task MustNotThrowWhenDeliveryPushedAfterStop()
        {
            var harness = ReceiverHarness.Create();

            await harness.Receiver.StopReceiver();

            Func<Task> pushAfterStop = () => harness.PushAsync(deliveryTag: 99);

            await pushAfterStop.Should().NotThrowAsync(
                "a delivery pushed after a terminal stop must be dropped, not forced into a completed buffer writer");
        }

        // Stop-then-dispose is idempotent: dispose performs the SAME surgical stop, and StopReceivingAsync is
        // gate-serialized and idempotent, so a second run finds a torn-down channel + cleared registration and
        // no-ops without throwing.
        [Fact]
        public async Task MustNotThrowWhenStopThenDispose()
        {
            var harness = ReceiverHarness.Create();

            await harness.Receiver.StopReceiver();

            Func<Task> disposeAfterStop = async () => await harness.Receiver.DisposeAsync();

            await disposeAfterStop.Should().NotThrowAsync("stop-then-dispose must be idempotent");
        }

        // Double-stop is idempotent: a second StopReceiver finds a torn-down channel + cleared registration and
        // no-ops without throwing.
        [Fact]
        public async Task MustNotThrowWhenStoppedTwice()
        {
            var harness = ReceiverHarness.Create();

            await harness.Receiver.StopReceiver();

            Func<Task> stopAgain = () => harness.Receiver.StopReceiver();

            await stopAgain.Should().NotThrowAsync("a double-stop must be a clean no-op");
        }

        // TERMINAL: a late automatic recovery AFTER a stop must NOT re-register a consumer — the source cleared the
        // stored registration delegate. Per the no-consumerless-committed-channel rule, a recovery with no delegate
        // stored commits NO channel at all (rather than committing an open-but-consumerless one), so there is nothing
        // to consume on and no restart-after-stop.
        [Fact]
        public async Task MustNotReRegisterConsumerOnRecoveryAfterStop()
        {
            var harness = ReceiverHarness.Create();

            await harness.Receiver.StopReceiver();
            await harness.ConnectionSource.SimulateRecoveryAsync();

            harness.ConnectionSource.ReceiveChannel.Should().BeNull(
                "a recovery after a terminal stop (no registration delegate stored) must commit NO receive channel, "
                + "so no consumerless channel is left behind");
        }

        // A stop that THROWS must still complete the buffer. IRabbitMqConnectionSource is a PUBLIC seam, so a
        // consumer-supplied source may fault StopReceivingAsync for any reason; if the buffer completion were
        // skipped when it does, the parked ReceiveMessageAsync pull would never return and HOST SHUTDOWN WOULD
        // HANG. The fault must still propagate — a foreign source's contract violation is never swallowed, it
        // simply can no longer strand the reader.
        [Fact]
        public async Task MustCompleteBufferWhenStopThrows()
        {
            var harness = ReceiverHarness.Create();
            var stopFault = new InvalidOperationException("the connection source's stop faulted");
            harness.ConnectionSource.StopFault = stopFault;
            var parkedReceive = harness.ReceiveAsync();

            Func<Task> stop = () => harness.Receiver.StopReceiver();

            (await stop.Should().ThrowAsync<InvalidOperationException>(
                "a foreign connection source's stop failure must still surface to the caller"))
                .Which.Should().BeSameAs(stopFault);
            await AssertParkedReceiveUnblocksAsync(parkedReceive);
        }

        // Awaits the parked pull under a BOUNDED timeout: an unbounded await would turn a stranded reader into a
        // HANGING test run instead of a failing assertion.
        private static async Task AssertParkedReceiveUnblocksAsync(Task<MessageBrokerContext> parkedReceive)
        {
            var completed = await Task.WhenAny(parkedReceive, Task.Delay(TimeSpan.FromSeconds(5)));

            completed.Should().BeSameAs(parkedReceive,
                "completing the buffer writer must unblock the parked pull, so a teardown cannot hang host shutdown");

            Func<Task> awaitParked = () => parkedReceive;
            await awaitParked.Should().ThrowAsync<ChannelClosedException>(
                "a completed buffer writer faults the parked read instead of leaving it waiting forever");
        }
    }
}
