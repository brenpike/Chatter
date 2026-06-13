using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using FluentAssertions;
using RabbitMQ.Client;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqConnectionSource
{
    // Pins the shutdown-ordering contract between RabbitMqConnectionSource and an outstanding
    // RabbitMqPublishChannelRental: a rental can outlive the source (the source is disposed while a publish is
    // still in flight). When the rental is then disposed it calls back into ReturnPublishChannel, which must
    // dispose the orphaned channel WITHOUT re-pooling it — the pool is gone — but MUST still release the publish
    // permit so the permit count is conserved (no publish waiter is stranded across teardown).
    //
    // These tests pin the OBSERVABLE dispose contract: no-throw on rental-return-after-dispose, the orphaned
    // channel is disposed, DisposeAsync is idempotent, AND the disposed check is enforced on BOTH sides of every
    // gate/permit acquisition (a gated/permit entrypoint queued behind DisposeAsync throws ObjectDisposedException
    // rather than resurrecting resources or hanging). They drive the production RabbitMqConnectionSource directly
    // through the InternalsVisibleTo reflection seam + RecordingChannel, deterministically, with no broker. The full
    // dispose-vs-in-flight-settle and dispose-vs-recovery races under REAL concurrency are only provable against a
    // live broker on the nightly Docker suite; these broker-free tests pin the observable contract the
    // gate-serialized teardown, the disposed-observed-on-both-sides helpers, and the never-disposed gates
    // (GATE LIFETIME) are designed to uphold.
    public class WhenDisposing : Testing.Core.Context
    {
        private static readonly FieldInfo ReceiveChannelGateField = typeof(RabbitMqConnectionSource)
            .GetField("_receiveChannelGate", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PublishPoolGateField = typeof(RabbitMqConnectionSource)
            .GetField("_publishPoolGate", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RegisterConsumerField = typeof(RabbitMqConnectionSource)
            .GetField("_registerConsumer", BindingFlags.Instance | BindingFlags.NonPublic);

        private static SemaphoreSlim ReceiveGate(RabbitMqConnectionSource source)
            => (SemaphoreSlim)ReceiveChannelGateField.GetValue(source);

        private static SemaphoreSlim PublishGate(RabbitMqConnectionSource source)
            => (SemaphoreSlim)PublishPoolGateField.GetValue(source);

        private static Func<IChannel, long, CancellationToken, Task> RegisteredConsumer(RabbitMqConnectionSource source)
            => (Func<IChannel, long, CancellationToken, Task>)RegisterConsumerField.GetValue(source);

        // A rental created via AcquirePublishChannelAsync always corresponds to ONE taken permit, so ReturnPublishChannel
        // is balanced. These broker-free tests construct the rental out-of-band, so they reflectively take the permit
        // first to mirror a real acquire — otherwise the unconditional Release in ReturnPublishChannel would overflow a
        // capacity-1 semaphore.
        private static RabbitMqConnectionSource NewSourceWithHeldPermit(out RabbitMqPublishChannelRental rental, out RecordingChannel channel)
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(), publishChannelPoolCapacity: 1);
            PublishGate(source).Wait();
            channel = new RecordingChannel();
            rental = new RabbitMqPublishChannelRental(source, channel);
            return source;
        }

        [Fact]
        public async Task MustNotThrowWhenRentalIsDisposedAfterSource()
        {
            var source = NewSourceWithHeldPermit(out var rental, out _);

            // Source disposed FIRST, rental disposed AFTER.
            await source.DisposeAsync();

            Func<Task> disposeRental = async () => await rental.DisposeAsync();

            await disposeRental.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustDisposeOrphanedChannelWhenRentalReturnedAfterSourceDisposed()
        {
            var source = NewSourceWithHeldPermit(out var rental, out var channel);

            await source.DisposeAsync();
            await rental.DisposeAsync();

            channel.Disposed.Should().BeTrue("the orphaned channel returned after dispose must be disposed, not re-pooled");
            PublishGate(source).CurrentCount.Should().Be(1,
                "ReturnPublishChannel must ALWAYS release the permit so the count is conserved across teardown");
        }

        // DisposeAsync is idempotent: a second call short-circuits on _disposed and is a clean no-op. The gates are
        // never disposed (GATE LIFETIME), so re-entry never touches a disposed semaphore.
        [Fact]
        public async Task MustNotThrowWhenDisposedTwice()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            await source.DisposeAsync();

            Func<Task> disposeAgain = async () => await source.DisposeAsync();

            await disposeAgain.Should().NotThrowAsync();
        }

        // Finding 1 (resurrection-after-dispose): a receive-gated entrypoint queued BEHIND DisposeAsync must observe
        // _disposed UNDER the gate after teardown releases it and throw ObjectDisposedException — it must NOT resurrect
        // a connection/channel nor overwrite _registerConsumer on the disposed singleton. Deterministic: hold the
        // receive gate, queue DisposeAsync behind it, release; then prove the post-dispose calls throw + leave
        // _registerConsumer untouched.
        [Fact]
        public async Task MustThrowAndNotResurrectWhenReceiveOpsRaceDispose()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var gate = ReceiveGate(source);

            // Hold the gate so DisposeAsync cannot acquire it yet.
            await gate.WaitAsync();
            var disposeTask = source.DisposeAsync().AsTask();

            // DisposeAsync is now queued behind the gate. Release so it acquires, sets _disposed under the gate, and
            // completes its teardown.
            gate.Release();
            await disposeTask;

            // RunOnReceiveChannelAsync queued after teardown observes _disposed (pre-wait fast path here, since the
            // gate is free) and throws — it never resurrects a channel/connection.
            Func<Task> runAfterDispose = async () =>
                await source.RunOnReceiveChannelAsync<object>((_, _) => Task.FromResult<object>(null), CancellationToken.None);
            await runAfterDispose.Should().ThrowAsync<ObjectDisposedException>();

            // StartReceivingAsync after teardown throws and does NOT overwrite _registerConsumer on the disposed singleton.
            Func<Task> startAfterDispose = async () =>
                await source.StartReceivingAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);
            await startAfterDispose.Should().ThrowAsync<ObjectDisposedException>();

            RegisteredConsumer(source).Should().BeNull("a disposed-rejected StartReceivingAsync must not store the delegate");
        }

        // Finding 1, the harder side: a caller already QUEUED INSIDE the gate (post-WaitAsync) when DisposeAsync runs.
        // Hold the gate via a long-running gated op, queue DisposeAsync behind it, queue a second receive op behind
        // DisposeAsync, then release. DisposeAsync sets _disposed under the gate; the second op then acquires the gate,
        // re-checks _disposed on the post-wait side, and throws rather than resurrecting resources.
        [Fact]
        public async Task MustRejectReceiveOpQueuedBehindDisposeOnPostWaitSide()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var gate = ReceiveGate(source);

            await gate.WaitAsync();

            // Queue DisposeAsync, then queue a receive op behind it — both block on the held gate, FIFO.
            var disposeTask = source.DisposeAsync().AsTask();
            Func<Task> queuedRun = async () =>
                await source.StartReceivingAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);
            var runTask = queuedRun.Should().ThrowAsync<ObjectDisposedException>();

            // Release: DisposeAsync acquires first (sets _disposed under the gate), releases; the queued op then
            // acquires and observes _disposed on the post-wait side.
            gate.Release();
            await disposeTask;
            await runTask;

            RegisteredConsumer(source).Should().BeNull("the post-wait disposed re-check must reject before storing the delegate");
        }

        // Finding 2 (publish-permit waiter not stranded): with capacity 1 exhausted, a second AcquirePublishChannelAsync
        // blocks on the permit. When the permit is released during/after teardown (ReturnPublishChannel always releases),
        // the blocked acquire WAKES, observes _disposed on the post-wait side, and throws ObjectDisposedException rather
        // than hanging. BOUNDED with a timeout so a regression FAILS FAST instead of hanging the suite.
        [Fact]
        public async Task MustWakeAndThrowBlockedPublishAcquireOnDispose()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"), publishChannelPoolCapacity: 1);
            var gate = PublishGate(source);

            // Exhaust the single permit (models a rental in flight).
            await gate.WaitAsync();

            // Second acquire blocks on the saturated permit.
            var blockedAcquire = source.AcquirePublishChannelAsync(CancellationToken.None);
            blockedAcquire.IsCompleted.Should().BeFalse("the second acquire must block while the only permit is held");

            // Dispose the source (sets _disposed; does NOT itself release the permit), then release the held permit to
            // model the in-flight rental returning via ReturnPublishChannel — which always releases.
            await source.DisposeAsync();
            gate.Release();

            // The blocked acquire must complete (throwing ODE) WITHOUT hanging. Bound it so a regression fails fast.
            var timeout = Task.Delay(TimeSpan.FromSeconds(5));
            var completed = await Task.WhenAny(blockedAcquire, timeout);
            completed.Should().BeSameAs((Task)blockedAcquire, "the blocked publish acquire must wake on dispose, not hang");

            Func<Task> awaitBlocked = async () => await blockedAcquire;
            await awaitBlocked.Should().ThrowAsync<ObjectDisposedException>(
                "a publish acquire woken after disposal must throw, not return a rental on a torn-down source");

            gate.CurrentCount.Should().Be(1,
                "the woken acquire must release the permit it transiently held so the count is conserved");
        }
    }
}
