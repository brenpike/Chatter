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
    // Pins EnsureConnectionAsync's STALE-REPLACE arm: an existing _connection that is NOT open (IsOpen false) is
    // unsubscribed from RecoverySucceededAsync, DisposeAsync'd, and replaced with a freshly-created, subscribed
    // connection. This is distinct from the cold-start arm (_connection == null, no dispose) and the open-fast-path
    // (IsOpen true, returned without dispose/replace). Drives the production source through the SetConnection and
    // SetCreateConnectionHook reflection seams from WhenDisposing, broker-free.
    public class WhenReplacingStaleConnection : Testing.Core.Context
    {
        private static readonly FieldInfo ConnectionField = typeof(RabbitMqConnectionSource)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CreateConnectionHookField = typeof(RabbitMqConnectionSource)
            .GetField("_createConnectionForTest", BindingFlags.Instance | BindingFlags.NonPublic);

        private static IConnection Connection(RabbitMqConnectionSource source)
            => (IConnection)ConnectionField.GetValue(source);

        private static void SetConnection(RabbitMqConnectionSource source, IConnection connection)
            => ConnectionField.SetValue(source, connection);

        private static void SetCreateConnectionHook(RabbitMqConnectionSource source, Func<CancellationToken, Task<IConnection>> hook)
            => CreateConnectionHookField.SetValue(source, hook);

        // Builds a connection mock that hands back a fresh RecordingChannel for the receive-channel recreate, so
        // StartReceivingAsync's registration (QoS + consume) succeeds on the replacement connection.
        private static Mock<IConnection> NewOpenConnectionServingRecordingChannels()
        {
            var connectionMock = new Mock<IConnection>();
            connectionMock.SetupGet(c => c.IsOpen).Returns(true);
            connectionMock
                .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                .Returns<CreateChannelOptions, CancellationToken>((_, _) => Task.FromResult<IChannel>(new RecordingChannel()));
            return connectionMock;
        }

        [Fact]
        public async Task MustUnsubscribeDisposeAndReplaceStaleConnection()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            // Seed a STALE connection: present but reporting IsOpen == false, so EnsureConnectionAsync takes the
            // replace arm (NOT the open-fast-path and NOT the cold-start null arm).
            var staleConnection = new Mock<IConnection>();
            staleConnection.SetupGet(c => c.IsOpen).Returns(false);
            SetConnection(source, staleConnection.Object);

            var freshConnection = NewOpenConnectionServingRecordingChannels();
            SetCreateConnectionHook(source, _ => Task.FromResult(freshConnection.Object));

            // StartReceivingAsync -> EnsureReceiveChannelAsync -> RecreateReceiveChannelAsync -> EnsureConnectionAsync
            // observes the stale (not-open) connection and replaces it.
            await source.StartReceivingAsync(
                (channel, _, ct) => Task.FromResult("consumer-tag"), CancellationToken.None);

            // The stale connection was unsubscribed from recovery BEFORE being disposed, then disposed.
            staleConnection.VerifyRemove(c => c.RecoverySucceededAsync -= It.IsAny<AsyncEventHandler<AsyncEventArgs>>(), Times.Once,
                "the stale connection must be unsubscribed from recovery before disposal");
            staleConnection.Verify(c => c.DisposeAsync(), Times.Once,
                "the stale (not-open) connection must be disposed before being replaced");

            // The fresh connection became the committed connection and was subscribed to recovery.
            Connection(source).Should().BeSameAs(freshConnection.Object, "the fresh connection replaces the stale one");
            freshConnection.VerifyAdd(c => c.RecoverySucceededAsync += It.IsAny<AsyncEventHandler<AsyncEventArgs>>(), Times.Once,
                "the replacement connection must subscribe the recovery handler");

            await source.DisposeAsync();
        }

        // Contrast arm: an OPEN existing connection is returned by the fast path WITHOUT being unsubscribed,
        // disposed, or replaced — proving the replace arm is gated on IsOpen == false, not taken unconditionally.
        [Fact]
        public async Task MustNotDisposeOrReplaceOpenConnection()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            var openConnection = NewOpenConnectionServingRecordingChannels();
            SetConnection(source, openConnection.Object);

            // The create hook must NOT be invoked: the open fast path returns the existing connection.
            var hookInvoked = false;
            SetCreateConnectionHook(source, _ =>
            {
                hookInvoked = true;
                return Task.FromResult(openConnection.Object);
            });

            await source.StartReceivingAsync(
                (channel, _, ct) => Task.FromResult("consumer-tag"), CancellationToken.None);

            hookInvoked.Should().BeFalse("an open existing connection must be returned by the fast path, not recreated");
            openConnection.Verify(c => c.DisposeAsync(), Times.Never, "an open connection must not be disposed by EnsureConnectionAsync");
            Connection(source).Should().BeSameAs(openConnection.Object);

            await source.DisposeAsync();
        }
    }
}
