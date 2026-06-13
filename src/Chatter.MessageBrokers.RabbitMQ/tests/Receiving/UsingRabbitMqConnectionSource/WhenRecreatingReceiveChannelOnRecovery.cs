using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver;
using FluentAssertions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqConnectionSource
{
    // Broker-free proof of the closed-by-construction epoch-lifecycle redesign (E-STEP-001..005). The source OWNS
    // the receive channel + consumer lifecycle: on every (re)creation — cold start, lazy recreate, automatic
    // recovery — it recreates the channel, bumps the epoch, and re-runs the receiver-supplied consume-registration
    // delegate UNDER THE GATE as ONE atomic event. Because the registration (which stamps the epoch onto deliveries)
    // and the bump are the same gated event, a delivery's stamped epoch ALWAYS equals the session that delivered it.
    //
    // This replaces the prior bump-only WhenAdvancingEpochOnRecovery suite, which asserted EDGE-1-only behavior
    // (epoch bumped on recovery) and masked EDGE 2 (the post-recovery consumer kept closing over the stale
    // pre-bump epoch, so every post-recovery settle no-op'd). The redesign eliminates BOTH edges by construction;
    // these tests prove it through the in-memory source + real receiver, which model the same lifecycle.
    public class WhenRecreatingReceiveChannelOnRecovery : Testing.Core.Context
    {
        // (a) Recovery recreates the channel, bumps the epoch, and re-runs the registration so the re-registered
        // consumer observes the NEW epoch — a delivery pushed after recovery carries the post-recovery epoch.
        [Fact]
        public async Task MustStampPostRecoveryEpochOnDeliveriesAfterRecovery()
        {
            var harness = ReceiverHarness.Create();
            var epochBefore = harness.ConnectionSource.CurrentReceiveChannelEpoch;

            await harness.ConnectionSource.SimulateRecoveryAsync();
            await harness.PushAsync(deliveryTag: 1);
            var context = await harness.ReceiveAsync();

            harness.ConnectionSource.CurrentReceiveChannelEpoch.Should().Be(epochBefore + 1,
                "recovery must recreate the channel and bump the epoch exactly once");
            context.BrokeredMessage.MessageContext[RabbitMqMessageContext.ChannelEpoch].Should().Be(epochBefore + 1,
                "the consumer re-registered on recovery must stamp the freshly-bumped epoch onto its deliveries");
        }

        // (b) A delivery stamped via the PRE-recovery registration no-ops at settle: it carries the old epoch, which
        // no longer matches the current epoch after recovery, so the receiver's guard makes the ack a no-op.
        [Fact]
        public async Task MustNoOpSettleForDeliveryStampedBeforeRecovery()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 7);
            var context = await harness.ReceiveAsync();
            var preRecoveryChannel = harness.ConnectionSource.ReceiveChannel;

            await harness.ConnectionSource.SimulateRecoveryAsync();

            var acked = await harness.Receiver.AckMessageAsync(context, transactionContext: null, CancellationToken.None);

            acked.Should().BeFalse("a delivery stamped under the pre-recovery epoch must no-op at settle");
            preRecoveryChannel.Acks.Should().BeEmpty("the recycled channel's delivery tag must never be acked");
            harness.ConnectionSource.ReceiveChannel.Acks.Should().BeEmpty("the post-recovery channel never saw this delivery tag");
        }

        // (c) A delivery stamped via the POST-recovery re-registration settles: it carries the current epoch and
        // acks on the post-recovery channel.
        [Fact]
        public async Task MustSettleDeliveryStampedAfterRecovery()
        {
            var harness = ReceiverHarness.Create();
            await harness.ConnectionSource.SimulateRecoveryAsync();

            await harness.PushAsync(deliveryTag: 21);
            var context = await harness.ReceiveAsync();

            var acked = await harness.Receiver.AckMessageAsync(context, transactionContext: null, CancellationToken.None);

            acked.Should().BeTrue("a delivery stamped under the post-recovery epoch settles");
            harness.ConnectionSource.ReceiveChannel.Acks.Single().DeliveryTag.Should().Be(21UL);
        }

        // A delivery buffered BEFORE recovery still surfaces AFTER recovery — the bounded buffer is created once and
        // is NOT recreated on re-registration, so in-flight deliveries survive the consumer swap. It still no-ops at
        // settle (it carries the pre-recovery epoch), proving the buffer survives independently of the epoch guard.
        [Fact]
        public async Task MustPreserveBufferedDeliveryAcrossRecovery()
        {
            var harness = ReceiverHarness.Create();
            await harness.PushAsync(deliveryTag: 33);

            await harness.ConnectionSource.SimulateRecoveryAsync();

            var context = await harness.ReceiveAsync();
            context.BrokeredMessage.MessageContext[RabbitMqMessageContext.DeliveryTag].Should().Be(33UL,
                "a delivery buffered before recovery survives the consumer re-registration");

            var acked = await harness.Receiver.AckMessageAsync(context, transactionContext: null, CancellationToken.None);
            acked.Should().BeFalse("the pre-recovery delivery still carries the stale epoch and no-ops at settle");
        }

        // (d) Bump + re-registration are atomic under the gate: rapid repeated recoveries each produce exactly one
        // bump and one re-registration, never an interleave. Each recovery yields a fresh channel carrying a
        // re-registered consumer, and the epoch advances exactly once per recovery.
        [Fact]
        public async Task MustBumpAndReRegisterExactlyOncePerRecovery()
        {
            var harness = ReceiverHarness.Create();
            var epochBefore = harness.ConnectionSource.CurrentReceiveChannelEpoch;

            await harness.ConnectionSource.SimulateRecoveryAsync();
            await harness.ConnectionSource.SimulateRecoveryAsync();
            await harness.ConnectionSource.SimulateRecoveryAsync();

            harness.ConnectionSource.CurrentReceiveChannelEpoch.Should().Be(epochBefore + 3,
                "the epoch must advance exactly once per recovery");
            // One initial channel + three recovery channels, each with its own re-registered consumer.
            harness.ConnectionSource.ReceiveChannels.Should().HaveCount(4);
            harness.ConnectionSource.ReceiveChannels.Should().OnlyContain(c => c.RegisteredConsumer != null,
                "every (re)created channel must carry a re-registered consumer");
        }

        // (e) Recovery firing after disposal is a clean no-op on the PRODUCTION source: a recovery callback can
        // still be queued by the client as the source tears down, and invoking the handler after disposal (when the
        // receive gate is already disposed) must not throw ObjectDisposedException out of the library's event
        // dispatch. Uses the same InternalsVisibleTo reflection seam the prior suite used to invoke the handler.
        private static readonly MethodInfo RecoveryHandler = typeof(RabbitMqConnectionSource)
            .GetMethod("OnRecoverySucceededAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        [Fact]
        public async Task MustNotThrowWhenRecoveryFiresAfterDisposal()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var handler = (AsyncEventHandler<AsyncEventArgs>)Delegate.CreateDelegate(
                typeof(AsyncEventHandler<AsyncEventArgs>), source, RecoveryHandler);

            await source.DisposeAsync();

            // After disposal the handler must short-circuit on the _disposed flag before touching the disposed gate
            // or attempting a recreate, so this is a clean no-op rather than a throw.
            Func<Task> raiseAfterDispose = () => handler(new Mock<IConnection>().Object, new AsyncEventArgs(CancellationToken.None));

            await raiseAfterDispose.Should().NotThrowAsync();
        }
    }
}
