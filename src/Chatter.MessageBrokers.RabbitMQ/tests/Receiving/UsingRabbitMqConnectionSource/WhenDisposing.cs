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
        // Lifecycle authority replaces the former bool _disposed: a single monotonic int (Live=0, Disposing=1,
        // Disposed=2). Tests read it to prove a surrendered op never advanced the source past its expected state.
        private static readonly FieldInfo LifecycleField = typeof(RabbitMqConnectionSource)
            .GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ConnectionField = typeof(RabbitMqConnectionSource)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CreateConnectionHookField = typeof(RabbitMqConnectionSource)
            .GetField("_createConnectionForTest", BindingFlags.Instance | BindingFlags.NonPublic);

        private static SemaphoreSlim ReceiveGate(RabbitMqConnectionSource source)
            => (SemaphoreSlim)ReceiveChannelGateField.GetValue(source);

        private static SemaphoreSlim PublishGate(RabbitMqConnectionSource source)
            => (SemaphoreSlim)PublishPoolGateField.GetValue(source);

        private static Func<IChannel, long, CancellationToken, Task> RegisteredConsumer(RabbitMqConnectionSource source)
            => (Func<IChannel, long, CancellationToken, Task>)RegisterConsumerField.GetValue(source);

        private static int Lifecycle(RabbitMqConnectionSource source)
            => (int)LifecycleField.GetValue(source);

        private static IConnection Connection(RabbitMqConnectionSource source)
            => (IConnection)ConnectionField.GetValue(source);

        private static void SetCreateConnectionHook(RabbitMqConnectionSource source, Func<CancellationToken, Task<IConnection>> hook)
            => CreateConnectionHookField.SetValue(source, hook);

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

        private const int LifecycleDisposed = 2;

        private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(5);

        // Waits for the task to settle (complete or fault) within RaceTimeout; if it does not, fails the test rather
        // than hanging the suite, so a regression that re-opens the create-vs-dispose race FAILS FAST. Does NOT
        // propagate the task's exception — the caller re-awaits the task for its own throw/no-throw assertion.
        private static async Task AwaitBoundedAsync(Task task, string because)
        {
            var completed = await Task.WhenAny(task, Task.Delay(RaceTimeout));
            completed.Should().BeSameAs(task, because);
        }

        // (a) PUBLISH-OR-SURRENDER (closed-by-construction): an AcquirePublishChannelAsync suspended INSIDE connection
        // creation across a completing DisposeAsync must SURRENDER — throw ObjectDisposedException, return NO live
        // rental, leave _connection null, NOT subscribe recovery, and conserve the publish permit — rather than
        // resurrecting a connection past teardown. The connection-create step runs UNDER the receive gate, so the
        // suspended acquire holds the gate and DisposeAsync must be admitted (CAS to Disposing) but left queued on the
        // gate until the acquire resumes-and-surrenders. Uses the authorized internal connection-create seam to suspend
        // mid-creation deterministically; timeout-bounded so a regression fails fast instead of hanging.
        [Fact]
        public async Task MustSurrenderWhenPublishAcquireSuspendedMidConnectionCreationRacesDispose()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"), publishChannelPoolCapacity: 1);

            // The seam suspends the connection-create step until the test releases it, then returns a fake connection
            // (IsOpen == false so EnsureConnectionAsync does not early-return). The fake records its own disposal so we
            // can prove the surrendered connection is disposed, not leaked.
            var creationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCreation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectionMock = new Mock<IConnection>();
            connectionMock.SetupGet(c => c.IsOpen).Returns(false);
            SetCreateConnectionHook(source, async _ =>
            {
                creationReached.SetResult();
                await releaseCreation.Task;
                return connectionMock.Object;
            });

            // Start the publish acquire; it takes the permit, acquires the receive gate to read-or-create the
            // connection (the connection-create step runs UNDER the receive gate), and suspends inside that step while
            // STILL HOLDING the receive gate.
            var acquireTask = source.AcquirePublishChannelAsync(CancellationToken.None);
            await creationReached.Task; // connection creation is now in flight, suspended on the seam, receive gate held

            // Admit DisposeAsync WITHOUT awaiting it to completion: its admission CAS (Live->Disposing) runs
            // synchronously before its first await, so the source is immediately torn, but its receive-gate wait is now
            // QUEUED behind the suspended acquire (which holds the gate). Awaiting dispose here would deadlock — the
            // acquire only releases the gate once it resumes, and it only resumes once the seam is released below.
            var disposeTask = source.DisposeAsync().AsTask();

            // Release the create step: the acquire resumes, observes the source torn (Disposing), surrenders the
            // just-created connection, throws, and releases the receive gate — which lets DisposeAsync complete.
            releaseCreation.SetResult();
            await AwaitBoundedAsync(acquireTask, "the suspended publish acquire must resume and surrender, not hang");

            Func<Task> awaitAcquire = async () => await acquireTask;
            await awaitAcquire.Should().ThrowAsync<ObjectDisposedException>(
                "a publish acquire suspended mid-creation across a completing dispose must surrender, not return a rental");

            await AwaitBoundedAsync(disposeTask, "DisposeAsync must complete once the surrendering acquire releases the gate");
            Lifecycle(source).Should().Be(LifecycleDisposed, "DisposeAsync completed after the acquire surrendered");

            connectionMock.Verify(c => c.DisposeAsync(), Times.Once,
                "the connection created mid-dispose must be disposed (surrendered), not leaked or assigned");
            connectionMock.VerifyAdd(c => c.RecoverySucceededAsync += It.IsAny<AsyncEventHandler<AsyncEventArgs>>(), Times.Never,
                "a surrendered connection must NOT have the recovery handler subscribed");
            Connection(source).Should().BeNull("the surrendered connection must never be assigned to _connection");
            PublishGate(source).CurrentCount.Should().Be(1,
                "the surrendering acquire must release its permit so the count is conserved");
        }

        // (b) The receive path's EnsureConnectionAsync suspended mid-creation across a completing DisposeAsync must
        // SURRENDER: the resumed op throws ObjectDisposedException, does NOT assign _connection (stays null), and does
        // NOT subscribe RecoverySucceededAsync (the connection mock is disposed, never wired). Here the receive gate is
        // HELD by the suspended op, so DisposeAsync's gate wait is queued behind it — the admission CAS to Disposing
        // still happens first, so the post-resume surrender re-check observes the torn source. Timeout-bounded.
        [Fact]
        public async Task MustSurrenderWhenReceiveConnectionCreationSuspendedRacesDispose()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));

            var creationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCreation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectionMock = new Mock<IConnection>();
            connectionMock.SetupGet(c => c.IsOpen).Returns(false);
            SetCreateConnectionHook(source, async _ =>
            {
                creationReached.SetResult();
                await releaseCreation.Task;
                return connectionMock.Object;
            });

            // StartReceivingAsync holds the receive gate and suspends inside connection creation.
            var startTask = source.StartReceivingAsync((_, _, _) => Task.CompletedTask, CancellationToken.None);
            await creationReached.Task;

            // DisposeAsync admits (CAS Live->Disposing) then queues on the receive gate the suspended op holds.
            var disposeTask = source.DisposeAsync().AsTask();

            // Release the create step: the receive op resumes, sees the source torn (Disposing was CAS'd), surrenders
            // the connection, throws, and releases the gate — which lets DisposeAsync complete.
            releaseCreation.SetResult();

            Func<Task> awaitStart = async () => await startTask;
            await awaitStart.Should().ThrowAsync<ObjectDisposedException>(
                "a receive-path connection creation suspended across a completing dispose must surrender");

            await AwaitBoundedAsync(disposeTask, "DisposeAsync must complete once the surrendering op releases the gate");

            Connection(source).Should().BeNull("the surrendered connection must never be assigned to _connection");
            connectionMock.Verify(c => c.DisposeAsync(), Times.Once,
                "the surrendered connection must be disposed");
            connectionMock.VerifyAdd(c => c.RecoverySucceededAsync += It.IsAny<AsyncEventHandler<AsyncEventArgs>>(), Times.Never,
                "a surrendered connection must NOT have the recovery handler subscribed");
        }

        // (c) Recovery-vs-dispose: a recovery callback entering the gated recreate while disposal is queued is a clean
        // no-op — no resurrection, no throw out of the client's event dispatch. Mirrors
        // WhenRecreatingReceiveChannelOnRecovery.MustNotThrowWhenRecoveryFiresAfterDisposal but pins it from the
        // disposing side and asserts the source ends Disposed with no connection resurrected.
        [Fact]
        public async Task MustNoOpRecoveryThatRacesDispose()
        {
            var source = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"));
            var recoveryHandlerField = typeof(RabbitMqConnectionSource)
                .GetMethod("OnRecoverySucceededAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var handler = (AsyncEventHandler<AsyncEventArgs>)Delegate.CreateDelegate(
                typeof(AsyncEventHandler<AsyncEventArgs>), source, recoveryHandlerField);

            await source.DisposeAsync();

            // Recovery firing after the source is torn must short-circuit (not throw, not resurrect a connection).
            Func<Task> raiseAfterDispose = () => handler(new Mock<IConnection>().Object, new AsyncEventArgs(CancellationToken.None));
            await raiseAfterDispose.Should().NotThrowAsync(
                "recovery firing after disposal must be a clean no-op out of the client's event dispatch");

            Lifecycle(source).Should().Be(LifecycleDisposed);
            Connection(source).Should().BeNull("recovery after dispose must not resurrect a connection");
        }
    }
}
