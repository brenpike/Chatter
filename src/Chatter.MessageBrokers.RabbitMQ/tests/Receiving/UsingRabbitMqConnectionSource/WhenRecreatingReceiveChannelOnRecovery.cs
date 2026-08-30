using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Generic;
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

            // A recycled channel makes this a settlement that was ATTEMPTED and did not happen: Failed, which the
            // core reports as a failed receive. The broker redelivers the delivery under the new epoch.
            acked.Outcome.Should().Be(SettlementOutcome.Failed, "a delivery stamped under the pre-recovery epoch must no-op at settle");
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

            acked.Outcome.Should().Be(SettlementOutcome.Settled, "a delivery stamped under the post-recovery epoch settles");
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
            acked.Outcome.Should().Be(SettlementOutcome.Failed, "the pre-recovery delivery still carries the stale epoch and no-ops at settle");
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

        // (f) NO CONSUMERLESS COMMITTED CHANNEL: a recovery that fires BEFORE the first StartReceivingAsync (the
        // publish-first scenario — the connection was materialized by a publish, then automatic recovery fires while
        // no consume-registration delegate is stored) must NOT commit a receive channel. Committing an open-but-
        // consumerless channel would let the eventual StartReceivingAsync take the IsOpen fast path in
        // EnsureReceiveChannelAsync and return WITHOUT registering a consumer, leaving the receiver permanently idle.
        // The recreate returns early leaving no committed channel, so the later registration runs a full
        // recreate-and-register and the receiver ends up with a live consumer.
        [Fact]
        public async Task MustNotCommitConsumerlessChannelWhenRecoveryFiresBeforeReceivingStarts()
        {
            var source = new InMemoryRabbitMqConnectionSource();
            var epochBefore = source.CurrentReceiveChannelEpoch;

            // Recovery fires before any StartReceivingAsync: no delegate is stored yet, so nothing to register.
            await source.SimulateRecoveryAsync();

            source.ReceiveChannel.Should().BeNull(
                "a recovery with no registration delegate stored must NOT commit a consumerless receive channel");
            source.CurrentReceiveChannelEpoch.Should().Be(epochBefore,
                "no channel was committed, so the observable epoch must be unchanged");

            // Now the receiver starts: StartReceivingAsync stores the delegate and forces a recreate-and-register,
            // so the source ends up with a live channel carrying a registered consumer (no permanently-idle receiver).
            string registeredEpochCaptured = null;
            await source.StartReceivingAsync((channel, epoch, ct) =>
            {
                registeredEpochCaptured = epoch.ToString();
                return Task.FromResult("consumer-tag");
            }, CancellationToken.None);

            source.ReceiveChannel.Should().NotBeNull("StartReceivingAsync must create and commit a receive channel");
            registeredEpochCaptured.Should().NotBeNull("the registration delegate must run on start");
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

        // NO-SILENT-IDLE (bounded retry re-establishes consumption): a non-ObjectDisposedException fault while
        // recreating the receive channel mid-recovery (here BasicConsumeAsync throwing on the freshly-recovered
        // channel) must NOT leave the receiver permanently idle. The handler retries the gated recreate, so a transient
        // fault that clears on a later attempt re-establishes the consumer — the recovery completes without throwing and
        // a fresh channel carrying a registered consumer is committed. Drives the PRODUCTION RabbitMqConnectionSource's
        // real OnRecoverySucceededAsync via the InternalsVisibleTo reflection seam + the connection-create seam, with a
        // channel that faults on its first consume attempt and succeeds thereafter — broker-free and deterministic.
        [Fact]
        public async Task MustRetryAndReEstablishConsumerWhenRecoveryRecreateFaultsTransiently()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            // Each CreateChannelAsync hands back a fresh fault-injecting channel; the first TWO consume attempts (the
            // cold-start StartReceivingAsync channel succeeds, then the FIRST recovery recreate channel faults) drive
            // the scenario. Fault the recovery channel's consume once, then let the retry's channel succeed.
            var channelFaults = new Queue<Exception>();
            var createdChannels = new List<FaultInjectingChannel>();
            var connectionMock = new Mock<IConnection>();
            connectionMock.SetupGet(c => c.IsOpen).Returns(true);
            connectionMock
                .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                .Returns<CreateChannelOptions, CancellationToken>((_, _) =>
                {
                    var consumeFault = channelFaults.Count > 0 ? channelFaults.Dequeue() : null;
                    var channel = new FaultInjectingChannel(consumeFault);
                    createdChannels.Add(channel);
                    return Task.FromResult<IChannel>(channel);
                });
            SetCreateConnectionHook(source, _ => Task.FromResult(connectionMock.Object));

            // The registration delegate calls BasicConsumeAsync on the supplied channel exactly as the real
            // RabbitMqReceiver.RegisterConsumerAsync does, so a FaultInjectingChannel whose consume faults makes the
            // recreate's registration step throw — the production fault path under test.
            Func<IChannel, long, CancellationToken, Task<string>> registerConsumer = (channel, _, ct) =>
                channel.BasicConsumeAsync(queue: "q", autoAck: false, consumerTag: string.Empty, noLocal: false,
                    exclusive: false, arguments: null, consumer: new AsyncEventingBasicConsumer(channel), cancellationToken: ct);

            // Cold start: stores the registration delegate and creates the first (clean) channel.
            await source.StartReceivingAsync(registerConsumer, CancellationToken.None);
            var epochAfterStart = source.CurrentReceiveChannelEpoch;

            // The FIRST recovery recreate's channel faults its consume once; the retry's channel succeeds.
            channelFaults.Enqueue(new OperationInterruptedException());

            var handler = (AsyncEventHandler<AsyncEventArgs>)Delegate.CreateDelegate(
                typeof(AsyncEventHandler<AsyncEventArgs>), source, RecoveryHandler);

            Func<Task> raiseRecovery = () => handler(connectionMock.Object, new AsyncEventArgs(CancellationToken.None));

            await raiseRecovery.Should().NotThrowAsync(
                "a transient recreate fault must be retried so the recovery re-establishes consumption, not throw");

            source.CurrentReceiveChannelEpoch.Should().Be(epochAfterStart + 1,
                "the successful retry commits exactly one epoch bump for the recovery");
            createdChannels.Last().RegisteredConsumer.Should().NotBeNull(
                "the retry must re-register the consumer on the freshly-created channel so the receiver is not idle");
            createdChannels.First(c => c.ConsumeFaulted).Disposed.Should().BeTrue(
                "the faulted recovery channel must be disposed by the transactional recreate before the retry");

            await source.DisposeAsync();
        }

        // NO-SILENT-IDLE (fault surfaced, not swallowed): a non-ObjectDisposedException fault that PERSISTS across every
        // bounded retry must be SURFACED out of the recovery handler (so the client's callback-exception dispatch makes
        // it observable) rather than swallowed into a permanent silent idle. The source stays LIVE throughout, so the
        // disposed-no-op path never applies; the persistent fault propagates. Contrast with
        // MustNotThrowWhenRecoveryFiresAfterDisposal, where a TORN source's recreate ODE is the clean no-op.
        [Fact]
        public async Task MustSurfaceFaultWhenRecoveryRecreatePersistentlyFaults()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            var faultEveryConsumeAfterStart = false;
            var connectionMock = new Mock<IConnection>();
            connectionMock.SetupGet(c => c.IsOpen).Returns(true);
            connectionMock
                .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                .Returns<CreateChannelOptions, CancellationToken>((_, _) =>
                {
                    var consumeFault = faultEveryConsumeAfterStart
                        ? new OperationInterruptedException()
                        : null;
                    return Task.FromResult<IChannel>(new FaultInjectingChannel(consumeFault));
                });
            SetCreateConnectionHook(source, _ => Task.FromResult(connectionMock.Object));

            Func<IChannel, long, CancellationToken, Task<string>> registerConsumer = (channel, _, ct) =>
                channel.BasicConsumeAsync(queue: "q", autoAck: false, consumerTag: string.Empty, noLocal: false,
                    exclusive: false, arguments: null, consumer: new AsyncEventingBasicConsumer(channel), cancellationToken: ct);

            // Cold start succeeds (no fault), then EVERY subsequent recovery recreate consume faults.
            await source.StartReceivingAsync(registerConsumer, CancellationToken.None);
            var epochAfterStart = source.CurrentReceiveChannelEpoch;
            faultEveryConsumeAfterStart = true;

            var handler = (AsyncEventHandler<AsyncEventArgs>)Delegate.CreateDelegate(
                typeof(AsyncEventHandler<AsyncEventArgs>), source, RecoveryHandler);

            Func<Task> raiseRecovery = () => handler(connectionMock.Object, new AsyncEventArgs(CancellationToken.None));

            await raiseRecovery.Should().ThrowAsync<OperationInterruptedException>(
                "a persistent recreate fault must surface out of the handler (observable failure), not be swallowed into a silent idle");

            source.CurrentReceiveChannelEpoch.Should().Be(epochAfterStart,
                "no recreate committed, so the observable epoch is unchanged by the failed recovery");

            await source.DisposeAsync();
        }

        // A fault-injecting IChannel for the production-source recovery tests: it captures consume registration and
        // QoS like RecordingChannel but can fault its consume (modelling BasicConsumeAsync throwing on the freshly
        // transport-recovered channel). Every other member throws NotImplementedException so an untested path surfaces.
        private sealed class FaultInjectingChannel : IChannel
        {
            private readonly Exception _consumeFault;

            public FaultInjectingChannel(Exception consumeFault)
            {
                _consumeFault = consumeFault;
            }

            public IAsyncBasicConsumer RegisteredConsumer { get; private set; }
            public bool ConsumeFaulted { get; private set; }
            public bool Disposed { get; private set; }

            public Task BasicQosAsync(uint prefetchSize, ushort prefetchCount, bool global, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<string> BasicConsumeAsync(string queue, bool autoAck, string consumerTag, bool noLocal, bool exclusive, IDictionary<string, object> arguments, IAsyncBasicConsumer consumer, CancellationToken cancellationToken = default)
            {
                if (_consumeFault is not null)
                {
                    ConsumeFaulted = true;
                    throw _consumeFault;
                }

                RegisteredConsumer = consumer;
                return Task.FromResult("in-memory-consumer-tag");
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

            public ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public ValueTask BasicPublishAsync<TProperties>(string exchange, string routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader => throw new NotImplementedException();
            public ValueTask BasicPublishAsync<TProperties>(CachedString exchange, CachedString routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader => throw new NotImplementedException();
            public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public Task BasicCancelAsync(string consumerTag, bool noWait = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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

        private static readonly FieldInfo CreateConnectionHookField = typeof(RabbitMqConnectionSource)
            .GetField("_createConnectionForTest", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void SetCreateConnectionHook(RabbitMqConnectionSource source, Func<CancellationToken, Task<IConnection>> hook)
            => CreateConnectionHookField.SetValue(source, hook);
    }
}
