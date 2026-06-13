using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using FluentAssertions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqConnectionSource
{
    // Broker-free wiring proof for the post-recovery false-ack fix (R-STEP-003). RabbitMQ.Client transparently
    // recovers the SAME receive IChannel on automatic recovery, so EnsureReceiveChannelAsync early-returns without
    // recreating it and would leave the epoch stale — letting an in-flight pre-recovery delivery tag pass the
    // stale-epoch settle guard and false-ack on the recovered session. The fix subscribes the source's
    // OnRecoverySucceededAsync handler to IConnection.RecoverySucceededAsync so each recovery advances the epoch.
    //
    // There is NO production injection seam for IConnection (the source creates it internally via
    // ConnectionFactory), and adding one would change the DI-registration contract — out of scope. So this test
    // uses the SAME reflection approach the existing test doubles already use (InMemoryRabbitMqConnectionSource /
    // RabbitMqPublishChannelRentalFactory reflect into the source's private members): it sets the private
    // _connection field to a Moq IConnection and subscribes the production handler to that mock's
    // RecoverySucceededAsync event exactly as EnsureConnectionAsync does, then raises the event through Moq and
    // asserts the epoch advanced — without ever contacting a broker.
    public class WhenAdvancingEpochOnRecovery : Testing.Core.Context
    {
        private static readonly MethodInfo RecoveryHandler = typeof(RabbitMqConnectionSource)
            .GetMethod("OnRecoverySucceededAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ConnectionField = typeof(RabbitMqConnectionSource)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ReceiveGateField = typeof(RabbitMqConnectionSource)
            .GetField("_receiveChannelGate", BindingFlags.Instance | BindingFlags.NonPublic);

        // Subscribes the production handler to the mock connection's recovery event the same way
        // EnsureConnectionAsync does, returning the typed delegate so the test can raise the event through Moq.
        private static AsyncEventHandler<AsyncEventArgs> Subscribe(RabbitMqConnectionSource source, Mock<IConnection> connection)
        {
            var handler = (AsyncEventHandler<AsyncEventArgs>)Delegate.CreateDelegate(
                typeof(AsyncEventHandler<AsyncEventArgs>), source, RecoveryHandler);
            ConnectionField.SetValue(source, connection.Object);
            connection.Object.RecoverySucceededAsync += handler;
            return handler;
        }

        [Fact]
        public async Task MustAdvanceEpochOncePerRecovery()
        {
            await using var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var connection = new Mock<IConnection>();
            Subscribe(source, connection);

            var epochBefore = source.CurrentReceiveChannelEpoch;

            connection.Raise(c => c.RecoverySucceededAsync += null, connection.Object, new AsyncEventArgs(CancellationToken.None));

            source.CurrentReceiveChannelEpoch.Should().Be(epochBefore + 1,
                "each successful automatic recovery must advance the receive-channel epoch so a delivery tag " +
                "captured under the pre-recovery epoch can no longer pass the stale-epoch settle guard");
        }

        [Fact]
        public async Task MustAdvanceEpochOnceForEachOfMultipleRecoveries()
        {
            await using var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var connection = new Mock<IConnection>();
            Subscribe(source, connection);

            var epochBefore = source.CurrentReceiveChannelEpoch;

            connection.Raise(c => c.RecoverySucceededAsync += null, connection.Object, new AsyncEventArgs(CancellationToken.None));
            connection.Raise(c => c.RecoverySucceededAsync += null, connection.Object, new AsyncEventArgs(CancellationToken.None));
            connection.Raise(c => c.RecoverySucceededAsync += null, connection.Object, new AsyncEventArgs(CancellationToken.None));

            source.CurrentReceiveChannelEpoch.Should().Be(epochBefore + 3,
                "the epoch must advance exactly once per recovery so successive reconnects each invalidate the " +
                "deliveries in flight at the time they occurred");
        }

        [Fact]
        public async Task MustAdvanceEpochUnderTheReceiveGate()
        {
            await using var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var connection = new Mock<IConnection>();
            var handler = Subscribe(source, connection);
            var receiveGate = (SemaphoreSlim)ReceiveGateField.GetValue(source);

            var epochBefore = source.CurrentReceiveChannelEpoch;

            // Hold the SAME gate RunOnReceiveChannelAsync holds during a settlement. The handler must block on it,
            // so the epoch cannot move while the gate is held — proving the bump is serialized against an in-flight
            // settle (the race the false-ack guard depends on being closed).
            await receiveGate.WaitAsync();
            var recovery = handler(connection.Object, new AsyncEventArgs(CancellationToken.None));

            await Task.Delay(50);
            recovery.IsCompleted.Should().BeFalse("the recovery handler must wait on the receive gate the settlement holds");
            source.CurrentReceiveChannelEpoch.Should().Be(epochBefore, "the epoch must not move while the gate is held");

            receiveGate.Release();
            await recovery;

            source.CurrentReceiveChannelEpoch.Should().Be(epochBefore + 1,
                "once the in-flight settlement releases the gate, the recovery handler acquires it and advances the epoch");
        }

        [Fact]
        public async Task MustNotThrowWhenRecoveryFiresAfterDisposal()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var connection = new Mock<IConnection>();
            var handler = Subscribe(source, connection);

            await source.DisposeAsync();

            // A recovery callback can still be queued by the client as the source tears down; invoking the handler
            // after disposal (when the receive gate is already disposed) must be a clean no-op, not throw
            // ObjectDisposedException out of the library's event dispatch.
            Func<Task> raiseAfterDispose = () => handler(connection.Object, new AsyncEventArgs(CancellationToken.None));

            await raiseAfterDispose.Should().NotThrowAsync();
        }
    }
}
