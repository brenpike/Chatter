using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
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
    // Pins the PRODUCTION RabbitMqConnectionSource publish-channel acquire/return lifecycle on a LIVE source:
    // AcquirePublishChannelAsync creates a publish channel (with confirms) off the connection and hands back a live
    // RabbitMqPublishChannelRental; disposing the rental RE-POOLS the channel when IsOpen == true; a second acquire
    // TryTakes the pooled channel (the reuse branch) rather than creating a fresh one. Also pins the null-arg guards
    // on RunOnReceiveChannelAsync / StartReceivingAsync. Drives the source via the connection-create reflection seam
    // + an open publish-channel double, broker-free.
    public class WhenAcquiringPublishChannel : Testing.Core.Context
    {
        private static readonly FieldInfo CreateConnectionHookField = typeof(RabbitMqConnectionSource)
            .GetField("_createConnectionForTest", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void SetCreateConnectionHook(RabbitMqConnectionSource source, Func<CancellationToken, Task<IConnection>> hook)
            => CreateConnectionHookField.SetValue(source, hook);

        // Builds a fake open connection that hands back a fresh open publish channel from a supplied queue per
        // CreateChannelAsync call, counting the creations so the reuse branch (no new create on the second acquire)
        // is assertable, and CAPTURING the CreateChannelOptions each publish-channel creation passed so a test can
        // assert publisher confirmations + confirmation tracking were enabled (the confirm-before-ack safety
        // invariant — a regression dropping them must FAIL the suite, not pass it).
        private static RabbitMqConnectionSource NewSourceServingPublishChannels(Queue<OpenPublishChannel> channelsToServe, out int[] createCount)
            => NewSourceServingPublishChannels(channelsToServe, out createCount, out _);

        private static RabbitMqConnectionSource NewSourceServingPublishChannels(Queue<OpenPublishChannel> channelsToServe,
                                                                                out int[] createCount,
                                                                                out List<CreateChannelOptions> capturedOptions)
        {
            var creations = new int[1];
            createCount = creations;
            var captured = new List<CreateChannelOptions>();
            capturedOptions = captured;
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var connectionMock = new Mock<IConnection>();
            connectionMock.SetupGet(c => c.IsOpen).Returns(true);
            connectionMock
                .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                .Returns<CreateChannelOptions, CancellationToken>((options, _) =>
                {
                    creations[0]++;
                    captured.Add(options);
                    return Task.FromResult<IChannel>(channelsToServe.Dequeue());
                });
            SetCreateConnectionHook(source, _ => Task.FromResult(connectionMock.Object));
            return source;
        }

        [Fact]
        public async Task MustReturnLiveRentalWithChannelOnAcquire()
        {
            var channels = new Queue<OpenPublishChannel>(new[] { new OpenPublishChannel() });
            var source = NewSourceServingPublishChannels(channels, out _);

            await using var rental = await source.AcquirePublishChannelAsync(CancellationToken.None);

            rental.Should().NotBeNull();
            rental.Channel.Should().NotBeNull("a live acquire hands back a rental carrying the rented channel");

            await source.DisposeAsync();
        }

        [Fact]
        public async Task MustRePoolChannelWhenRentalDisposedAndChannelOpen()
        {
            var channel = new OpenPublishChannel();
            var channels = new Queue<OpenPublishChannel>(new[] { channel });
            var source = NewSourceServingPublishChannels(channels, out _);

            var rental = await source.AcquirePublishChannelAsync(CancellationToken.None);
            await rental.DisposeAsync();

            channel.Disposed.Should().BeFalse("an open channel returned by a rental is re-pooled, not disposed");

            await source.DisposeAsync();
        }

        [Fact]
        public async Task MustReusePooledChannelOnSecondAcquire()
        {
            var first = new OpenPublishChannel();
            // Only ONE channel is ever served: if the second acquire created a fresh one the queue would underflow,
            // proving the second acquire must reuse the re-pooled channel.
            var channels = new Queue<OpenPublishChannel>(new[] { first });
            var source = NewSourceServingPublishChannels(channels, out var createCount);

            var firstRental = await source.AcquirePublishChannelAsync(CancellationToken.None);
            await firstRental.DisposeAsync();

            await using var secondRental = await source.AcquirePublishChannelAsync(CancellationToken.None);

            createCount[0].Should().Be(1, "the second acquire must TryTake the re-pooled channel, not create a fresh one");
            secondRental.Channel.Should().BeSameAs(first, "the second acquire reuses the same re-pooled channel");

            await source.DisposeAsync();
        }

        // The publish channel MUST be created with publisher confirmations AND confirmation tracking enabled: this
        // is the adapter's confirm-before-ack/publish safety invariant (RabbitMQ.Client 7.2.1 faults an unroutable
        // mandatory publish via confirm-tracking, so an unroutable publish surfaces as a Dispatch failure rather
        // than silent loss). Asserting the captured CreateChannelOptions makes a regression in
        // CreatePublishChannelAsync that dropped either flag FAIL this suite instead of passing it.
        [Fact]
        public async Task MustCreatePublishChannelWithPublisherConfirmationsEnabled()
        {
            var channels = new Queue<OpenPublishChannel>(new[] { new OpenPublishChannel() });
            var source = NewSourceServingPublishChannels(channels, out _, out var capturedOptions);

            await using var rental = await source.AcquirePublishChannelAsync(CancellationToken.None);

            var options = capturedOptions.Should().ContainSingle("the acquire creates exactly one publish channel").Subject;
            options.Should().NotBeNull("the publish channel must be created with explicit confirm options, not the default null");
            options.PublisherConfirmationsEnabled.Should().BeTrue("publisher confirmations must be enabled (confirm-before-ack safety)");
            options.PublisherConfirmationTrackingEnabled.Should().BeTrue("publisher confirmation tracking must be enabled so an unroutable mandatory publish faults rather than silently dropping");

            await source.DisposeAsync();
        }

        // --- null-arg guards (the early ArgumentNullException before any gate acquisition) ---

        [Fact]
        public async Task MustThrowOnNullRunOnReceiveChannelOperation()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            Func<Task> act = async () => await source.RunOnReceiveChannelAsync<object>(null, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();

            await source.DisposeAsync();
        }

        [Fact]
        public async Task MustThrowOnNullStartReceivingRegistration()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            Func<Task> act = async () => await source.StartReceivingAsync(null, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentNullException>();

            await source.DisposeAsync();
        }

        // An open publish channel double: reports IsOpen == true so ReturnPublishChannel RE-POOLS it (unlike
        // RecordingChannel, which is hardwired IsOpen == false and would always be disposed on return). Records its
        // disposal so a re-pool test can assert it was NOT disposed. Every untested member throws so an untested path
        // surfaces.
        private sealed class OpenPublishChannel : IChannel
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
