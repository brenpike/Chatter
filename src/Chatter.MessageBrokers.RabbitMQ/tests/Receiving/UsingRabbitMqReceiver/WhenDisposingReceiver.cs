using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver
{
    // Broker-free proof that the receiver's dispose is a SURGICAL STOP and NEVER a teardown of the shared singleton
    // IRabbitMqConnectionSource (closes issue #367). Two distinct ways the old escalating dispose killed process-wide
    // messaging, both closed here:
    //   1. RabbitMqReceiver was registered Scoped and publicly resolvable, and MSDI disposes scoped IDisposables at
    //      scope end — so ANY consumer scope that merely resolved the receiver (a health check, a stray injection into
    //      a request-scoped service) tore down the AMQP connection and drained the publish channel pool.
    //   2. The core's BrokeredMessageReceiver.StartReceiver returns the receiver itself as an IAsyncDisposable and its
    //      hosted service await-usings it, so a NORMAL receiver shutdown reached DisposeAsync on the INITIALIZED
    //      receiver — closing the connection and draining the publish pool that the SENDER shares, while hosted
    //      services stopping later were still publishing.
    // Either way the failure surfaced far from its cause, as ObjectDisposedException on a later publish. Dispose now
    // does exactly what StopReceiver does — cancel this receiver's consumer and tear down the RECEIVE CHANNEL only,
    // then complete the buffer — matching the sibling ASB adapter, whose DisposeAsync never touches the shared
    // ServiceBusClient. Path 1 is now closed at the container: the receiver is not published into DI, so no scope can
    // resolve one. With no stray instance to distinguish, the stop runs on every dispose path and the SOURCE decides
    // whether there is a registered consumer to cancel.
    public class WhenDisposingReceiver : Testing.Core.Context
    {
        private const string ReceiverPath = "orders-queue";
        private const string ErrorPath = "orders-error";

        // A receiver that never initialized still runs the receive-side stop on the synchronous container-dispose
        // path — and must still leave the shared singleton source's own lifetime entirely alone.
        [Fact]
        public void MustNotDisposeConnectionSourceWhenNeverInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            receiver.Dispose();

            connectionSource.DisposeCount.Should().Be(0,
                "a receiver that never initialized owns no share of the singleton source's lifetime, so its dispose "
                + "must not tear down the process-wide connection and publish pool");
            connectionSource.StopCount.Should().Be(1,
                "the surgical receive-side stop runs unconditionally; the source decides there is nothing registered "
                + "to cancel");
        }

        [Fact]
        public async Task MustNotDisposeConnectionSourceAsynchronouslyWhenNeverInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            await receiver.DisposeAsync();

            connectionSource.DisposeCount.Should().Be(0,
                "the async dispose path must leave the shared source's lifetime alone exactly as the sync path does");
            connectionSource.StopCount.Should().Be(1,
                "the async dispose path runs the same unconditional receive-side stop");
        }

        // The whole point: the receive-side stop is SURGICAL, so after an uninitialized receiver is disposed the shared
        // source is still LIVE and the sender keeps publishing. Proven positively — a publish channel rental is still
        // handed out.
        [Fact]
        public async Task MustLeaveConnectionSourceUsableWhenNeverInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            await receiver.DisposeAsync();

            await using var rental = await connectionSource.AcquirePublishChannelAsync(CancellationToken.None);
            rental.Channel.Should().NotBeNull(
                "the sender's publish path must survive a stray uninitialized receiver's disposal");
            connectionSource.StopCount.Should().Be(1,
                "the stop that ran was the receive-side one, which leaves the connection and publish pool untouched");
        }

        // THE SHUTDOWN GUARD: the core's BrokeredMessageReceiver.StartReceiver returns the receiver as an
        // IAsyncDisposable and its hosted service await-usings it, so this path runs on EVERY receiver shutdown — not
        // only at process teardown. Disposing the shared singleton source here would close the AMQP connection and
        // drain the publish pool underneath a sender that later-stopping hosted services are still using. Dispose must
        // stop THIS receiver's consumer and nothing else.
        [Fact]
        public async Task MustStopReceivingWithoutDisposingConnectionSourceAsynchronouslyWhenInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = await CreateInitializedReceiverAsync(connectionSource);

            await receiver.DisposeAsync();

            connectionSource.AsyncDisposeCount.Should().Be(0,
                "the singleton source is shared with the sender, so no receiver may tear it down");
            connectionSource.StopCount.Should().Be(1,
                "dispose must perform the surgical receive-side stop instead");
        }

        // The sync container-dispose path makes the same surgical stop, blocking on it.
        [Fact]
        public async Task MustStopReceivingWithoutDisposingConnectionSourceSynchronouslyWhenInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = await CreateInitializedReceiverAsync(connectionSource);

            receiver.Dispose();

            connectionSource.SyncDisposeCount.Should().Be(0,
                "the synchronous container-dispose path must not tear down the shared source either");
            connectionSource.StopCount.Should().Be(1,
                "the sync path must block on the same surgical receive-side stop");
        }

        // The whole point, proven positively on the initialized path: after an initialized receiver is disposed the shared
        // source is still LIVE, so the sender keeps publishing. The double mirrors the production source by throwing
        // ObjectDisposedException from AcquirePublishChannelAsync once torn down, so this cannot go green against a
        // disposed source.
        [Fact]
        public async Task MustLeaveConnectionSourceUsableWhenInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = await CreateInitializedReceiverAsync(connectionSource);

            await receiver.DisposeAsync();

            await using var rental = await connectionSource.AcquirePublishChannelAsync(CancellationToken.None);
            rental.Channel.Should().NotBeNull(
                "a hosted service that stops after the receiver must still be able to publish");
        }

        // A receiver whose InitializeAsync THREW must still run the stop on dispose: the source stores the
        // consume-registration delegate BEFORE the receive channel is ensured, so a fault mid-registration leaves the
        // delegate stored and only the stop clears it. An unconditional stop covers this no matter where the fault
        // landed — and it still never disposes the shared source.
        [Fact]
        public async Task MustStopReceivingWithoutDisposingConnectionSourceWhenInitializationThrew()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = CreateReceiver(connectionSource);
            Func<Task> initialize = () => receiver.InitializeAsync(new ReceiverOptions
            {
                MessageReceiverPath = ReceiverPath,
                TransactionMode = TransactionMode.ReceiveOnly
            }, CancellationToken.None);

            await initialize.Should().ThrowAsync<InvalidOperationException>(
                "neither a dead-letter nor an error queue is configured, so startup must reject the receiver");

            await receiver.DisposeAsync();

            connectionSource.DisposeCount.Should().Be(0,
                "a failed startup grants no authority over the shared singleton source's lifetime");
            connectionSource.StopCount.Should().Be(1,
                "dispose runs the stop unconditionally, so a failed initialization still clears any consume "
                + "registration the source already stored");
        }

        [Fact]
        public async Task MustNotThrowWhenUninitializedReceiverIsDisposedTwice()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            await receiver.DisposeAsync();
            Func<Task> disposeAgain = async () => await receiver.DisposeAsync();

            await disposeAgain.Should().NotThrowAsync("double-dispose must stay idempotent");
            connectionSource.DisposeCount.Should().Be(0,
                "no number of disposals of an uninitialized receiver may tear down the shared singleton source");
        }

        [Fact]
        public async Task MustNotThrowWhenInitializedReceiverIsDisposedTwice()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = await CreateInitializedReceiverAsync(connectionSource);

            await receiver.DisposeAsync();
            Func<Task> disposeAgain = async () => await receiver.DisposeAsync();

            await disposeAgain.Should().NotThrowAsync("double-dispose must stay idempotent");
            connectionSource.DisposeCount.Should().Be(0,
                "no number of disposals of a receiver may tear down the shared singleton source");
        }

        // The surgical stop reaches the real seam: over the in-memory source, disposing an initialized receiver
        // cancels the AMQP consumer that BasicConsumeAsync registered and tears the receive channel down — the same
        // teardown StopReceiver performs.
        [Fact]
        public async Task MustCancelConsumerAndTearDownReceiveChannelOnDispose()
        {
            var harness = ReceiverHarness.Create();
            var receiveChannel = harness.ConnectionSource.ReceiveChannel;

            await harness.Receiver.DisposeAsync();

            receiveChannel.CancelledConsumerTags.Should().ContainSingle()
                .Which.Should().Be("in-memory-consumer-tag",
                    "dispose must cancel the AMQP consumer this receiver registered");
            receiveChannel.Disposed.Should().BeTrue("dispose must tear down the receive channel");
            harness.ConnectionSource.ReceivingStopped.Should().BeTrue("the source must record the terminal stop");
        }

        // SURGICAL over the real seam: dispose must leave the connection and publish pool intact so the sender — which
        // shares the singleton source and may stop later — keeps publishing.
        [Fact]
        public async Task MustNotTearDownPublishPathOnDispose()
        {
            var harness = ReceiverHarness.Create();

            await harness.Receiver.DisposeAsync();

            await using var rental = await harness.ConnectionSource.AcquirePublishChannelAsync(CancellationToken.None);
            rental.Should().NotBeNull("the sender's publish path must still work after the receiver is disposed");
            rental.Channel.Should().NotBeNull("a publish channel must still be acquirable after dispose");
        }

        // A delivery the broker might still try to push AFTER dispose is safely DROPPED (the consumer is cancelled and
        // the channel torn down) rather than thrown into the completed buffer writer.
        [Fact]
        public async Task MustNotThrowWhenDeliveryPushedAfterDispose()
        {
            var harness = ReceiverHarness.Create();

            await harness.Receiver.DisposeAsync();

            Func<Task> pushAfterDispose = () => harness.PushAsync(deliveryTag: 99);

            await pushAfterDispose.Should().NotThrowAsync(
                "a delivery pushed after dispose must be dropped, not forced into a completed buffer writer");
        }

        // Dispose runs the receive-side stop UNCONDITIONALLY — there is no per-instance initialization flag deciding
        // whether it runs. One receiver exists per AddRabbitMq registration (constructed at the single site in
        // Extensions.cs; it is neither container-resolvable nor publicly constructible), so "is this the real
        // receiver?" is no longer a question dispose has to answer. Stop-authority is the SOURCE's own registration
        // state, which is what StopReceivingAsync clears. Asserted through ReceivingStopped, which ONLY StopReceivingAsync writes, so this
        // fact goes red when dispose skips the stop.
        [Fact]
        public async Task MustStopReceivingWhenDisposedWithoutEverInitializing()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            await receiver.DisposeAsync();

            connectionSource.ReceivingStopped.Should().BeTrue(
                "dispose must run the source's receive-side stop on every path, so no stored consume registration "
                + "survives a receiver that never reached the end of InitializeAsync");
        }

        // The payoff of the unconditional stop: the stop cleared the source's registration delegate, so a LATE
        // automatic recovery re-registers nothing and — per the no-consumerless-committed-channel rule — commits no
        // receive channel at all. Without the stop the source would keep whatever registration it had stored and a
        // recovery could put a consumer back on a receiver that is already disposed.
        [Fact]
        public async Task MustNotReRegisterConsumerOnRecoveryAfterDisposingAnUninitializedReceiver()
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            await receiver.DisposeAsync();
            await connectionSource.SimulateRecoveryAsync();

            connectionSource.ReceiveChannel.Should().BeNull(
                "a recovery after a terminal stop (no registration delegate stored) must commit NO receive channel, "
                + "so no consumerless channel is left behind");
        }

        // A dispose whose stop THROWS must still complete the buffer. IRabbitMqConnectionSource is a PUBLIC seam, so
        // a consumer-supplied source may fault StopReceivingAsync for any reason; if the buffer completion were
        // skipped when it does, the parked ReceiveMessageAsync pull would never return and HOST SHUTDOWN WOULD HANG
        // — on the very path the core's hosted service await-usings. The fault must still propagate.
        [Fact]
        public async Task MustCompleteBufferWhenAsyncDisposeStopThrows()
        {
            var harness = ReceiverHarness.Create();
            var stopFault = new InvalidOperationException("the connection source's stop faulted");
            harness.ConnectionSource.StopFault = stopFault;
            var parkedReceive = harness.ReceiveAsync();

            Func<Task> dispose = async () => await harness.Receiver.DisposeAsync();

            (await dispose.Should().ThrowAsync<InvalidOperationException>(
                "a foreign connection source's stop failure must still surface to the caller"))
                .Which.Should().BeSameAs(stopFault);
            await AssertParkedReceiveUnblocksAsync(parkedReceive);
        }

        // The synchronous container-dispose path carries the same guarantee: it blocks on the stop via
        // GetAwaiter().GetResult(), which rethrows the fault UNWRAPPED (not as an AggregateException), and the
        // buffer must still be completed so no parked pull survives the throw.
        [Fact]
        public async Task MustCompleteBufferWhenSynchronousDisposeStopThrows()
        {
            var harness = ReceiverHarness.Create();
            var stopFault = new InvalidOperationException("the connection source's stop faulted");
            harness.ConnectionSource.StopFault = stopFault;
            var parkedReceive = harness.ReceiveAsync();

            Action dispose = () => harness.Receiver.Dispose();

            dispose.Should().Throw<InvalidOperationException>(
                "the blocking dispose path must surface a foreign connection source's stop failure too")
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

        private static RabbitMqReceiver CreateReceiver(IRabbitMqConnectionSource connectionSource)
        {
            var bodyConverterFactory = new BodyConverterFactory(new IBrokeredMessageBodyConverter[]
            {
                new RabbitMqBodyConverter(),
                new JsonBodyConverter()
            });
            return new RabbitMqReceiver(connectionSource,
                                        new RabbitMqOptions(hostName: "localhost"),
                                        bodyConverterFactory,
                                        Mock.Of<ILogger<RabbitMqReceiver>>());
        }

        private static async Task<RabbitMqReceiver> CreateInitializedReceiverAsync(DisposalRecordingConnectionSource connectionSource)
        {
            var receiver = CreateReceiver(connectionSource);
            await receiver.InitializeAsync(new ReceiverOptions
            {
                MessageReceiverPath = ReceiverPath,
                ErrorQueuePath = ErrorPath,
                TransactionMode = TransactionMode.ReceiveOnly
            }, CancellationToken.None);
            return receiver;
        }

        // A disposal-recording source, defined HERE rather than on the shared InMemoryRabbitMqConnectionSource so the
        // sibling suites' publish-channel assertions stay untouched. Implements BOTH IDisposable and IAsyncDisposable,
        // matching the production source, so a receiver that reached for EITHER teardown would be caught; and it
        // mirrors the production post-teardown contract — AcquirePublishChannelAsync throws ObjectDisposedException
        // once disposed — so "still usable" cannot go green against a torn-down source.
        private sealed class DisposalRecordingConnectionSource : IRabbitMqConnectionSource, IDisposable
        {
            private readonly RabbitMqPublishChannelRentalFactory _rentalFactory = new RabbitMqPublishChannelRentalFactory();

            public int SyncDisposeCount { get; private set; }
            public int AsyncDisposeCount { get; private set; }
            public int DisposeCount => SyncDisposeCount + AsyncDisposeCount;

            // Counts the SURGICAL receive-side teardown, so a test distinguishes "stopped consuming" from "tore down
            // the shared connection and publish pool".
            public int StopCount { get; private set; }

            public long CurrentReceiveChannelEpoch => 0;

            public void Dispose() => SyncDisposeCount++;

            public ValueTask DisposeAsync()
            {
                AsyncDisposeCount++;
                return default;
            }

            public Task StartReceivingAsync(Func<IChannel, long, CancellationToken, Task<string>> registerConsumer,
                                            CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task StopReceivingAsync(CancellationToken cancellationToken)
            {
                StopCount++;
                return Task.CompletedTask;
            }

            public Task<TResult> RunOnReceiveChannelAsync<TResult>(Func<IChannel, long, Task<TResult>> operation,
                                                                   CancellationToken cancellationToken)
                => throw new NotImplementedException();

            public Task<RabbitMqPublishChannelRental> AcquirePublishChannelAsync(CancellationToken cancellationToken)
            {
                if (DisposeCount > 0)
                {
                    throw new ObjectDisposedException(nameof(DisposalRecordingConnectionSource));
                }

                return Task.FromResult(_rentalFactory.Create(new RecordingChannel()));
            }
        }
    }
}
