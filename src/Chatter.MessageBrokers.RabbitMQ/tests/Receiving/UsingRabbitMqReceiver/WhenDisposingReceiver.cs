using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver
{
    // Broker-free proof that the receiver's dispose ESCALATION to the shared singleton IRabbitMqConnectionSource is
    // gated on an INITIALIZATION CLAIM (closes issue #367). RabbitMqReceiver is registered Scoped and is publicly
    // resolvable, and MSDI disposes scoped IDisposables at scope end — so ANY consumer scope that merely resolves the
    // receiver (a health check, a stray injection into a request-scoped service) used to tear down the process-wide
    // AMQP connection and drain the publish channel pool, killing the sender too. The failure then surfaced far from
    // its cause, as ObjectDisposedException on a later publish or settlement. The claim is latched at the TOP of
    // InitializeAsync, so the receiver the core actually initialized keeps today's FULL terminal teardown (the core's
    // BrokeredMessageReceiver terminal path calls DisposeAsync and must still release the connection), while an
    // uninitialized instance disposes as a surgical no-op.
    public class WhenDisposingReceiver : Testing.Core.Context
    {
        private const string ReceiverPath = "orders-queue";
        private const string ErrorPath = "orders-error";

        // An UNCLAIMED receiver — resolved by a stray scope and never initialized — must leave the shared singleton
        // source alone on the synchronous container-dispose path.
        [Fact]
        public void MustNotDisposeConnectionSourceWhenNeverInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            receiver.Dispose();

            connectionSource.DisposeCount.Should().Be(0,
                "a receiver that never initialized owns no share of the singleton source's lifetime, so its dispose "
                + "must not tear down the process-wide connection and publish pool");
        }

        [Fact]
        public async Task MustNotDisposeConnectionSourceAsynchronouslyWhenNeverInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            await receiver.DisposeAsync();

            connectionSource.DisposeCount.Should().Be(0,
                "the async dispose path must be gated on the same initialization claim as the sync path");
        }

        // The whole point of the gate: after a stray scope disposes an uninitialized receiver the shared source is
        // still LIVE, so the sender keeps publishing. Proven positively — a publish channel rental is still handed out.
        [Fact]
        public async Task MustLeaveConnectionSourceUsableWhenNeverInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = CreateReceiver(connectionSource);

            await receiver.DisposeAsync();

            await using var rental = await connectionSource.AcquirePublishChannelAsync(CancellationToken.None);
            rental.Channel.Should().NotBeNull(
                "the sender's publish path must survive a stray uninitialized receiver's disposal");
        }

        // REGRESSION GUARD for the terminal path: the core's BrokeredMessageReceiver teardown calls DisposeAsync on
        // the infrastructure receiver, and that is the ONLY thing that releases the AMQP connection at process
        // shutdown. A CLAIMED receiver must keep escalating, byte for byte with today's behaviour.
        [Fact]
        public async Task MustDisposeConnectionSourceAsynchronouslyWhenInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = await CreateInitializedReceiverAsync(connectionSource);

            await receiver.DisposeAsync();

            connectionSource.AsyncDisposeCount.Should().Be(1,
                "the initialized receiver owns the terminal teardown that releases the connection at shutdown");
        }

        // The sync path prefers the source's synchronous teardown (the container's sync dispose path), unchanged.
        [Fact]
        public async Task MustDisposeConnectionSourceSynchronouslyWhenInitialized()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = await CreateInitializedReceiverAsync(connectionSource);

            receiver.Dispose();

            connectionSource.SyncDisposeCount.Should().Be(1,
                "the sync dispose path must still prefer the source's synchronous teardown for a claimed receiver");
        }

        // A receiver whose InitializeAsync THREW is ALREADY claimed — the latch sits above every startup gate — so its
        // dispose still escalates, exactly as it did before the claim existed. Nothing about a failed startup changes.
        [Fact]
        public async Task MustDisposeConnectionSourceWhenInitializationThrew()
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

            connectionSource.AsyncDisposeCount.Should().Be(1,
                "the claim is latched above the startup gates, so a failed initialization still owns the teardown");
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
                "a second dispose of an unclaimed receiver must remain a no-op");
        }

        [Fact]
        public async Task MustNotThrowWhenInitializedReceiverIsDisposedTwice()
        {
            var connectionSource = new DisposalRecordingConnectionSource();
            var receiver = await CreateInitializedReceiverAsync(connectionSource);

            await receiver.DisposeAsync();
            Func<Task> disposeAgain = async () => await receiver.DisposeAsync();

            await disposeAgain.Should().NotThrowAsync("double-dispose must stay idempotent");
        }

        private static RabbitMqReceiver CreateReceiver(DisposalRecordingConnectionSource connectionSource)
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
        // sibling suites' publish-channel assertions stay untouched. Implements BOTH IDisposable (the synchronous
        // container teardown path the receiver's Dispose prefers) and IAsyncDisposable, matching the production
        // source, and mirrors its post-teardown contract: AcquirePublishChannelAsync throws ObjectDisposedException
        // once disposed, so "still usable" cannot go green against a torn-down source.
        private sealed class DisposalRecordingConnectionSource : IRabbitMqConnectionSource, IDisposable
        {
            private readonly RabbitMqPublishChannelRentalFactory _rentalFactory = new RabbitMqPublishChannelRentalFactory();

            public int SyncDisposeCount { get; private set; }
            public int AsyncDisposeCount { get; private set; }
            public int DisposeCount => SyncDisposeCount + AsyncDisposeCount;

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

            public Task StopReceivingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
