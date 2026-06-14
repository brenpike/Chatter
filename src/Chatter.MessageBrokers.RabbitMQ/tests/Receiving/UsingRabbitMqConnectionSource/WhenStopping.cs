using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.TestSupport;
using FluentAssertions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqConnectionSource
{
    // Pins the PRODUCTION RabbitMqConnectionSource.StopReceivingAsync surgical terminal teardown: under the receive
    // gate it cancels the registered consumer on the current receive channel, disposes+nulls the receive channel,
    // and CLEARS _registerConsumer/_consumerTag — while leaving the connection and the publish pool intact. Drives
    // the source via the connection-create reflection seam + IConnection/IChannel doubles (the same pattern as
    // WhenRecreatingReceiveChannelOnRecovery / WhenDisposing), broker-free.
    public class WhenStopping : Testing.Core.Context
    {
        private static readonly FieldInfo CreateConnectionHookField = typeof(RabbitMqConnectionSource)
            .GetField("_createConnectionForTest", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ReceiveChannelField = typeof(RabbitMqConnectionSource)
            .GetField("_receiveChannel", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RegisterConsumerField = typeof(RabbitMqConnectionSource)
            .GetField("_registerConsumer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ConsumerTagField = typeof(RabbitMqConnectionSource)
            .GetField("_consumerTag", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ConnectionField = typeof(RabbitMqConnectionSource)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void SetCreateConnectionHook(RabbitMqConnectionSource source, Func<CancellationToken, Task<IConnection>> hook)
            => CreateConnectionHookField.SetValue(source, hook);

        private static IChannel ReceiveChannel(RabbitMqConnectionSource source)
            => (IChannel)ReceiveChannelField.GetValue(source);

        private static object RegisterConsumer(RabbitMqConnectionSource source)
            => RegisterConsumerField.GetValue(source);

        private static string ConsumerTag(RabbitMqConnectionSource source)
            => (string)ConsumerTagField.GetValue(source);

        private static IConnection Connection(RabbitMqConnectionSource source)
            => (IConnection)ConnectionField.GetValue(source);

        // Wires a source to a fake open connection that hands back a single cancellable receive channel, then runs a
        // cold-start StartReceivingAsync (storing the registration delegate + consumer tag) so the source is in the
        // "receiving" state a stop tears down. Returns the receive channel the source committed.
        private static async Task<RabbitMqConnectionSource> NewReceivingSourceAsync(CancellableReceiveChannel receiveChannel,
                                                                                    IConnection connection = null)
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var connectionMock = connection;
            if (connectionMock is null)
            {
                var mock = new Mock<IConnection>();
                mock.SetupGet(c => c.IsOpen).Returns(true);
                mock.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                    .Returns<CreateChannelOptions, CancellationToken>((_, _) => Task.FromResult<IChannel>(receiveChannel));
                connectionMock = mock.Object;
            }
            SetCreateConnectionHook(source, _ => Task.FromResult(connectionMock));

            await source.StartReceivingAsync(
                (channel, _, ct) => Task.FromResult("the-consumer-tag"), CancellationToken.None);

            return source;
        }

        [Fact]
        public async Task MustCancelConsumerDisposeChannelAndClearRegistrationOnStop()
        {
            var receiveChannel = new CancellableReceiveChannel();
            var source = await NewReceivingSourceAsync(receiveChannel);

            await source.StopReceivingAsync(CancellationToken.None);

            receiveChannel.CancelledConsumerTags.Should().ContainSingle().Which.Should().Be("the-consumer-tag",
                "stop must cancel the stored consumer on the current receive channel");
            receiveChannel.Disposed.Should().BeTrue("stop must dispose the receive channel");
            ReceiveChannel(source).Should().BeNull("stop must null the receive channel");
            RegisterConsumer(source).Should().BeNull("stop must clear the registration delegate (terminal)");
            ConsumerTag(source).Should().BeNull("stop must clear the consumer tag (terminal)");
        }

        // The connection and the publish pool are deliberately left intact by a surgical stop: the source is a
        // process singleton shared with the sender, so the publish path must keep working after the receiver stops.
        // This asserts sender-OBSERVABLE liveness, not just retained field state: after stop the real publish
        // entrypoint (AcquirePublishChannelAsync) must still hand back a live rental carrying a usable channel. A
        // regression that tore the lifecycle down on stop, or rejected later acquires, would leave _connection
        // assigned and the connection undisposed yet FAIL this acquire — which retained-state assertions alone miss.
        [Fact]
        public async Task MustLeaveConnectionAndPublishPoolIntactOnStop()
        {
            var receiveChannel = new CancellableReceiveChannel();
            // The connection serves the receive channel for the cold-start, then a DISTINCT open publish channel for
            // the post-stop acquire (proving the publish path is exercised, not the already-disposed receive channel).
            var publishChannel = new OpenStopPublishChannel();
            var served = new Queue<IChannel>(new IChannel[] { receiveChannel, publishChannel });
            var connectionMock = new Mock<IConnection>();
            connectionMock.SetupGet(c => c.IsOpen).Returns(true);
            connectionMock.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                .Returns<CreateChannelOptions, CancellationToken>((_, _) => Task.FromResult(served.Dequeue()));
            var source = await NewReceivingSourceAsync(receiveChannel, connectionMock.Object);

            await source.StopReceivingAsync(CancellationToken.None);

            connectionMock.Verify(c => c.DisposeAsync(), Times.Never, "stop must not dispose the shared connection");
            Connection(source).Should().BeSameAs(connectionMock.Object, "stop leaves the connection intact for the sender");

            // Sender-observable proof: the publish pool is still usable after the receiver stopped.
            await using (var rental = await source.AcquirePublishChannelAsync(CancellationToken.None))
            {
                rental.Should().NotBeNull("the publish path must keep serving rentals after the receiver stops");
                rental.Channel.Should().BeSameAs(publishChannel,
                    "a post-stop acquire hands back a live rental carrying the freshly-created publish channel");
            }

            await source.DisposeAsync();
        }

        // The BasicCancelAsync try/catch swallows AlreadyClosed/ObjectDisposed: a channel whose cancel throws
        // AlreadyClosedException (the consumer is implicitly cancelled when the channel is already gone) must NOT
        // fault the stop — the channel is still disposed and the registration still cleared.
        [Fact]
        public async Task MustSwallowAlreadyClosedFromBasicCancelOnStop()
        {
            var receiveChannel = new CancellableReceiveChannel(cancelFault: RabbitMqExceptionFactory.AlreadyClosed());
            var source = await NewReceivingSourceAsync(receiveChannel);

            Func<Task> stop = async () => await source.StopReceivingAsync(CancellationToken.None);

            await stop.Should().NotThrowAsync("an AlreadyClosedException from BasicCancelAsync is swallowed as a no-op");
            receiveChannel.Disposed.Should().BeTrue("the channel is still disposed after the swallowed cancel fault");
            RegisterConsumer(source).Should().BeNull("the registration is still cleared after the swallowed cancel fault");
        }

        // Idempotent double-stop: the second stop finds a null channel + null registration and no-ops (no second
        // cancel, no throw).
        [Fact]
        public async Task MustNoOpOnSecondStop()
        {
            var receiveChannel = new CancellableReceiveChannel();
            var source = await NewReceivingSourceAsync(receiveChannel);

            await source.StopReceivingAsync(CancellationToken.None);
            Func<Task> stopAgain = async () => await source.StopReceivingAsync(CancellationToken.None);

            await stopAgain.Should().NotThrowAsync("a double-stop finds a null channel + null registration and no-ops");
            receiveChannel.CancelledConsumerTags.Should().ContainSingle("the second stop must not cancel again");
        }

        // A stop AFTER DisposeAsync is a clean no-op: RunReceiveGatedAsync throws ObjectDisposedException on the
        // torn source, which StopReceivingAsync swallows (a disposed source has nothing left to stop).
        [Fact]
        public async Task MustNoOpOnStopAfterDispose()
        {
            var receiveChannel = new CancellableReceiveChannel();
            var source = await NewReceivingSourceAsync(receiveChannel);

            await source.DisposeAsync();

            Func<Task> stopAfterDispose = async () => await source.StopReceivingAsync(CancellationToken.None);

            await stopAfterDispose.Should().NotThrowAsync("a terminal stop on an already-disposed source is a clean no-op");
        }

        // A receive IChannel that records the cancelled consumer tag and its disposal, and can optionally fault its
        // BasicCancelAsync to drive the AlreadyClosed/ObjectDisposed swallow arm. Reports IsOpen so the source's
        // EnsureReceiveChannelAsync commits it. Every untested member throws so an untested path surfaces.
        private sealed class CancellableReceiveChannel : IChannel
        {
            private readonly Exception _cancelFault;

            public CancellableReceiveChannel(Exception cancelFault = null)
            {
                _cancelFault = cancelFault;
            }

            public List<string> CancelledConsumerTags { get; } = new List<string>();
            public bool Disposed { get; private set; }

            public Task BasicQosAsync(uint prefetchSize, ushort prefetchCount, bool global, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task BasicCancelAsync(string consumerTag, bool noWait = false, CancellationToken cancellationToken = default)
            {
                CancelledConsumerTags.Add(consumerTag);
                if (_cancelFault is not null)
                {
                    throw _cancelFault;
                }
                return Task.CompletedTask;
            }

            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return default;
            }

            public bool IsOpen => true;
            public bool IsClosed => false;

            // --- Unused IChannel surface: a production path reaching any of these is untested by design ---------
            public int ChannelNumber => throw new NotImplementedException();
            public ShutdownEventArgs CloseReason => throw new NotImplementedException();
            public IAsyncBasicConsumer DefaultConsumer { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
            public string CurrentQueue => throw new NotImplementedException();
            public TimeSpan ContinuationTimeout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

            public event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync { add { } remove { } }
            public event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync { add { } remove { } }
            public event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync { add { } remove { } }
            public event AsyncEventHandler<CallbackExceptionEventArgs> CallbackExceptionAsync { add { } remove { } }
            public event AsyncEventHandler<FlowControlEventArgs> FlowControlAsync { add { } remove { } }
            public event AsyncEventHandler<ShutdownEventArgs> ChannelShutdownAsync { add { } remove { } }

            public Task<string> BasicConsumeAsync(string queue, bool autoAck, string consumerTag, bool noLocal, bool exclusive, IDictionary<string, object> arguments, IAsyncBasicConsumer consumer, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicPublishAsync<TProperties>(string exchange, string routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader => throw new NotImplementedException();
            public ValueTask BasicPublishAsync<TProperties>(CachedString exchange, CachedString routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader => throw new NotImplementedException();
            public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<BasicGetResult> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task CloseAsync(ushort replyCode, string replyText, bool abort, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task CloseAsync(ShutdownEventArgs reason, bool abort) => throw new NotImplementedException();
            public Task CloseAsync(ShutdownEventArgs reason, bool abort, CancellationToken cancellationToken) => throw new NotImplementedException();
            public Task ExchangeDeclareAsync(string exchange, string type, bool durable, bool autoDelete, IDictionary<string, object> arguments = null, bool passive = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ExchangeDeclarePassiveAsync(string exchange, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ExchangeDeleteAsync(string exchange, bool ifUnused = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ExchangeBindAsync(string destination, string source, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ExchangeUnbindAsync(string destination, string source, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<QueueDeclareOk> QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object> arguments = null, bool passive = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<QueueDeclareOk> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<uint> QueueDeleteAsync(string queue, bool ifUnused, bool ifEmpty, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<uint> QueuePurgeAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task QueueBindAsync(string queue, string exchange, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task QueueUnbindAsync(string queue, string exchange, string routingKey, IDictionary<string, object> arguments = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<uint> MessageCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<uint> ConsumerCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task TxCommitAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task TxRollbackAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task TxSelectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        }

        // An open publish-channel double for the post-stop acquire: reports IsOpen == true so the source commits it as
        // a live rental (and re-pools rather than disposes it on return). DISTINCT from the receive channel so the
        // post-stop publish path is genuinely exercised. Every untested member throws so an untested path surfaces.
        private sealed class OpenStopPublishChannel : IChannel
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return default;
            }

            public bool IsOpen => true;
            public bool IsClosed => false;

            // --- Unused IChannel surface: a production path reaching any of these is untested by design ---------
            public int ChannelNumber => throw new NotImplementedException();
            public ShutdownEventArgs CloseReason => throw new NotImplementedException();
            public IAsyncBasicConsumer DefaultConsumer { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
            public string CurrentQueue => throw new NotImplementedException();
            public TimeSpan ContinuationTimeout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

            public event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync { add { } remove { } }
            public event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync { add { } remove { } }
            public event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync { add { } remove { } }
            public event AsyncEventHandler<CallbackExceptionEventArgs> CallbackExceptionAsync { add { } remove { } }
            public event AsyncEventHandler<FlowControlEventArgs> FlowControlAsync { add { } remove { } }
            public event AsyncEventHandler<ShutdownEventArgs> ChannelShutdownAsync { add { } remove { } }

            public Task BasicQosAsync(uint prefetchSize, ushort prefetchCount, bool global, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<string> BasicConsumeAsync(string queue, bool autoAck, string consumerTag, bool noLocal, bool exclusive, IDictionary<string, object> arguments, IAsyncBasicConsumer consumer, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task BasicCancelAsync(string consumerTag, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicPublishAsync<TProperties>(string exchange, string routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader => throw new NotImplementedException();
            public ValueTask BasicPublishAsync<TProperties>(CachedString exchange, CachedString routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader => throw new NotImplementedException();
            public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<BasicGetResult> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task CloseAsync(ushort replyCode, string replyText, bool abort, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task CloseAsync(ShutdownEventArgs reason, bool abort) => throw new NotImplementedException();
            public Task CloseAsync(ShutdownEventArgs reason, bool abort, CancellationToken cancellationToken) => throw new NotImplementedException();
            public Task ExchangeDeclareAsync(string exchange, string type, bool durable, bool autoDelete, IDictionary<string, object> arguments = null, bool passive = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ExchangeDeclarePassiveAsync(string exchange, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ExchangeDeleteAsync(string exchange, bool ifUnused = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ExchangeBindAsync(string destination, string source, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task ExchangeUnbindAsync(string destination, string source, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<QueueDeclareOk> QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object> arguments = null, bool passive = false, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<QueueDeclareOk> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<uint> QueueDeleteAsync(string queue, bool ifUnused, bool ifEmpty, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<uint> QueuePurgeAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task QueueBindAsync(string queue, string exchange, string routingKey, IDictionary<string, object> arguments = null, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task QueueUnbindAsync(string queue, string exchange, string routingKey, IDictionary<string, object> arguments = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<uint> MessageCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task<uint> ConsumerCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task TxCommitAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task TxRollbackAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task TxSelectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        }
    }
}
