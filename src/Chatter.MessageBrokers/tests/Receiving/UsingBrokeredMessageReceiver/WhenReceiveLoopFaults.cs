#nullable disable

using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    // INVARIANT: drives the receive-loop fault paths that the happy-path tests never reach: a loop Task that becomes
    // IsFaulted (because the notify epilogue itself threw), the inline receive-error ladder (a failure to RECEIVE,
    // handled on the loop thread), and the empty-receive continue branch. NullLogger keeps the critical-log calls
    // no-ops; gating primitives + a 15s watchdog convert any teardown/drain regression into a Timeout, not a hang.
    public class WhenReceiveLoopFaults : Testing.Core.Context
    {
        private static MessageBrokerOptions BuildOptions(TransactionMode mode = TransactionMode.None)
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = mode;
            return opts;
        }

        // INVARIANT: pass-through IRecoveryStrategy invokes the delegate exactly once, no retry machinery. Tests that
        // need a faulting receive override only the MessageBrokerContext overload via FaultingThenPassThroughRecovery.
        private static Mock<IRecoveryStrategy> PassThroughRecovery()
        {
            var mock = new Mock<IRecoveryStrategy>();
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((action, _) => action());
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>((action, _) => action());
            return mock;
        }

        private static ReceiverOptions BuildReceiverOptions(int maxConcurrentCalls)
            => new ReceiverOptions
            {
                InfrastructureType = InMemoryMessagingInfrastructureProvider.InfrastructureType,
                MessageReceiverPath = "test-queue",
                SendingPath = "test-queue",
                ErrorQueuePath = "error-queue",
                DeadLetterQueuePath = "deadletter-queue",
                TransactionMode = TransactionMode.None,
                MaxReceiveAttempts = 10,
                MaxConcurrentCalls = maxConcurrentCalls,
            };

        private static MessageBrokerContext BuildContext()
        {
            var converter = new JsonBodyConverter();
            var body = converter.Convert(new FakeMessage { Value = "hello" });
            return new MessageBrokerContext(
                messageId: Guid.NewGuid().ToString(),
                body: body,
                applicationProperties: new Dictionary<string, object>(),
                messageReceiverPath: "test-queue",
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: converter);
        }

        private BrokeredMessageReceiver<FakeMessage> CreateSut(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            Mock<IReceivedMessageDispatcher> dispatcher,
            Mock<IRecoveryStrategy> recovery = null,
            Mock<ICriticalFailureNotifier> criticalNotifier = null)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);
            recovery ??= PassThroughRecovery();
            criticalNotifier ??= new Mock<ICriticalFailureNotifier>();

            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: criticalNotifier.Object,
                recoveryStrategy: recovery.Object,
                receivedMessageDispatcher: dispatcher.Object);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken watchdog)
        {
            while (!predicate())
            {
                watchdog.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }

        // ------------------------------------------------------------------ (a) faulted-loop belt-and-suspenders drain

        [Fact]
        public async Task MustDrainAndStopCleanlyWhenLoopTaskFaultedFromNotifyEpilogue()
        {
            const int maxConcurrentCalls = 2;
            const int messageCount = 2;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // One worker faults critically; the other (sibling) gates in-flight. The Notify epilogue then throws a
            // NON-critical InvalidOperationException, so MessageReceiverLoopAsync's finally throws and the loop Task
            // becomes IsFaulted BEFORE its own drain runs — leaving the sibling still gated. StopReceiver must SKIP the
            // faulted-loop await (the !IsFaulted guard) and run its belt-and-suspenders finally drain so the sibling
            // quiesces once we release the gate, then record Stop and dispose without throwing.
            using var siblingReleaseGate = new SemaphoreSlim(0, 1);
            var siblingEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = new StrongBox<int>(0);
            var faultArmed = new StrongBox<int>(0);

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    // The faulter waits until the sibling has entered (and is in the in-flight set) before throwing, so
                    // the loop genuinely has a gated sibling left undrained when the notify epilogue faults the loop.
                    if (Interlocked.Increment(ref faultArmed.Value) == 1)
                    {
                        await siblingEntered.Task;
                        throw new CriticalReceiverException("boom");
                    }

                    Interlocked.Increment(ref inFlight.Value);
                    siblingEntered.TrySetResult(true);
                    try
                    {
                        // Wait WITHOUT the worker token so cancellation (SignalLoopCriticalFault) can NOT self-quiesce the
                        // sibling. The ONLY release is the explicit gate release the test issues right before StopReceiver,
                        // so the sibling stays genuinely in-flight until StopReceiver's belt-and-suspenders drain awaits it.
                        await siblingReleaseGate.WaitAsync();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight.Value);
                    }
                });

            var loopFaulted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var criticalNotifier = new Mock<ICriticalFailureNotifier>();
            criticalNotifier
                .Setup(n => n.Notify(It.IsAny<FailureContext>()))
                .Returns((FailureContext _) =>
                {
                    loopFaulted.TrySetResult(true);
                    // Non-critical throw inside the epilogue: this escapes MessageReceiverLoopAsync's finally and faults
                    // the loop Task before its own drain executes.
                    throw new InvalidOperationException("notify failed");
                });

            var sut = CreateSut(infraReceiver, dispatcher, criticalNotifier: criticalNotifier);

            using var cts = new CancellationTokenSource();
            // Do NOT await StartReceiver's loop here: StartReceiverImpl awaits the (now faulting) loop, so the
            // StartReceiver task would surface the InvalidOperationException unless IsReceiving has flipped. Run it
            // detached; the receiver instance is reachable via sut for the teardown calls.
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var siblingCompleted = await Task.WhenAny(siblingEntered.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            siblingCompleted.Should().BeSameAs(siblingEntered.Task, "the sibling worker must gate in-flight");

            var faultCompleted = await Task.WhenAny(loopFaulted.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            faultCompleted.Should().BeSameAs(loopFaulted.Task, "the notify epilogue must run (and then fault the loop)");
            await loopFaulted.Task;

            // Release the gate so the StopReceiver drain can quiesce the sibling, then stop. StopReceiver must not throw
            // despite the loop having faulted.
            siblingReleaseGate.Release();

            var stop = sut.StopReceiver();
            var stopCompleted = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15)));
            stopCompleted.Should().BeSameAs(stop, "StopReceiver must skip the faulted loop, drain, and complete without throwing");
            await stop;

            Volatile.Read(ref inFlight.Value).Should().Be(0, "the belt-and-suspenders drain must quiesce the gated sibling");
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Stop, "StopReceiver must stop the infrastructure receiver even when the loop faulted");
        }

        // ------------------------------------------------------------------ (a') faulted-loop teardown via DisposeAsync (TOCTOU regression witness)

        [Fact]
        public async Task MustDisposeCleanlyWhenLoopTaskFaultedFromNotifyEpilogue()
        {
            const int maxConcurrentCalls = 2;
            const int messageCount = 2;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // Exact twin of MustDrainAndStopCleanlyWhenLoopTaskFaultedFromNotifyEpilogue, but tears down via DisposeAsync.
            // Regression witness for the now-fixed DisposeAsync TOCTOU: DisposeAsync previously had a !IsFaulted guard plus
            // an OCE-only catch, so a NON-OCE faulted loop (here, the notify epilogue throwing InvalidOperationException)
            // would either be skipped or escape. STEP-001 routed DisposeAsync through the shared observe-and-swallow quiesce
            // contract, so DisposeAsync must now SKIP/observe the faulted-loop await, drain the gated sibling, tear down the
            // infrastructure receiver, and complete without throwing.
            using var siblingReleaseGate = new SemaphoreSlim(0, 1);
            var siblingEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = new StrongBox<int>(0);
            var faultArmed = new StrongBox<int>(0);

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    // The faulter waits until the sibling has entered (and is in the in-flight set) before throwing, so
                    // the loop genuinely has a gated sibling left undrained when the notify epilogue faults the loop.
                    if (Interlocked.Increment(ref faultArmed.Value) == 1)
                    {
                        await siblingEntered.Task;
                        throw new CriticalReceiverException("boom");
                    }

                    Interlocked.Increment(ref inFlight.Value);
                    siblingEntered.TrySetResult(true);
                    try
                    {
                        // Wait WITHOUT the worker token so cancellation (SignalLoopCriticalFault) can NOT self-quiesce the
                        // sibling. The ONLY release is the explicit gate release the test issues right before DisposeAsync,
                        // so the sibling stays genuinely in-flight until DisposeAsync's belt-and-suspenders drain awaits it.
                        await siblingReleaseGate.WaitAsync();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight.Value);
                    }
                });

            var loopFaulted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var criticalNotifier = new Mock<ICriticalFailureNotifier>();
            criticalNotifier
                .Setup(n => n.Notify(It.IsAny<FailureContext>()))
                .Returns((FailureContext _) =>
                {
                    loopFaulted.TrySetResult(true);
                    // Non-critical throw inside the epilogue: this escapes MessageReceiverLoopAsync's finally and faults
                    // the loop Task before its own drain executes.
                    throw new InvalidOperationException("notify failed");
                });

            var sut = CreateSut(infraReceiver, dispatcher, criticalNotifier: criticalNotifier);

            using var cts = new CancellationTokenSource();
            // Do NOT await StartReceiver's loop here: StartReceiverImpl awaits the (now faulting) loop, so the
            // StartReceiver task would surface the InvalidOperationException unless IsReceiving has flipped. Run it
            // detached; the receiver instance is reachable via sut for the teardown calls.
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var siblingCompleted = await Task.WhenAny(siblingEntered.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            siblingCompleted.Should().BeSameAs(siblingEntered.Task, "the sibling worker must gate in-flight");

            var faultCompleted = await Task.WhenAny(loopFaulted.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            faultCompleted.Should().BeSameAs(loopFaulted.Task, "the notify epilogue must run (and then fault the loop)");
            await loopFaulted.Task;

            // Release the gate so the DisposeAsync drain can quiesce the sibling, then dispose. DisposeAsync must not throw
            // despite the loop having faulted with a NON-OCE exception (the regression the TOCTOU fix closed).
            siblingReleaseGate.Release();

            var dispose = sut.DisposeAsync().AsTask();
            var disposeCompleted = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(15)));
            disposeCompleted.Should().BeSameAs(dispose, "DisposeAsync must skip/observe the faulted loop, drain, and complete without throwing");
            await dispose;

            Volatile.Read(ref inFlight.Value).Should().Be(0, "the belt-and-suspenders drain must quiesce the gated sibling");
            // DisposeAsync's infra teardown calls _infrastructureReceiver.DisposeAsync(), which the fake records as
            // ReceiverCall.Dispose via RecordCallOnce (see InMemoryMessagingInfrastructureReceiver.DisposeAsync).
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Dispose, "DisposeAsync must tear down the infrastructure receiver even when the loop faulted");
        }

        // ------------------------------------------------------------------ (b) inline receive-error ladder

        [Fact]
        public async Task MustReleaseSlotAndContinueWhenReceiveThrowsGenericError()
        {
            const int messageCount = 1;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            infraReceiver.Enqueue(BuildContext());

            // The IRecoveryStrategy MessageBrokerContext overload throws a generic Exception on its FIRST call, then
            // delegates through. The loop's catch(Exception) handles the failed receive inline on the loop thread,
            // releases the slot, and continues pulling — eventually the real receive succeeds and the message is acked.
            // No Nack/Deadletter is recorded (those are worker-side dispositions, not receive-error handling).
            var receiveCalls = new StrongBox<int>(0);
            var recovery = new Mock<IRecoveryStrategy>();
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((action, _) =>
                {
                    if (Interlocked.Increment(ref receiveCalls.Value) == 1)
                    {
                        throw new Exception("transient receive failure");
                    }
                    return action();
                });
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>((action, _) => action());

            var dispatcher = new Mock<IReceivedMessageDispatcher>();

            var sut = CreateSut(infraReceiver, dispatcher, recovery: recovery);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls: 1), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Despite the first receive throwing, the loop continues pulling and the message is ultimately acked.
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Ack), watchdog.Token);

            cts.Cancel();
            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop, "the loop must continue after an inline receive error and cancel cleanly");
            await loop;

            receiveCalls.Value.Should().BeGreaterThan(1, "the first receive threw and the loop must have continued to receive again");
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Ack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack, "a failure to RECEIVE must not Nack");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter, "a failure to RECEIVE must not Deadletter");
        }

        // ------------------------------------------------------------------ (c) messageContext == null continue branch

        [Fact]
        public async Task MustReleaseSlotAndContinueWhenReceiveReturnsNull()
        {
            const int messageCount = 1;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            infraReceiver.Enqueue(BuildContext());

            // The receive overload returns null on its FIRST call (an empty receive), then delegates through. The loop's
            // messageContext == null branch releases the slot and continues; the next pull receives the real message,
            // which is acked. Proves the empty-receive continue path does not leak a slot or spawn a worker.
            var receiveCalls = new StrongBox<int>(0);
            var recovery = new Mock<IRecoveryStrategy>();
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((action, _) =>
                {
                    if (Interlocked.Increment(ref receiveCalls.Value) == 1)
                    {
                        return Task.FromResult<MessageBrokerContext>(null);
                    }
                    return action();
                });
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>((action, _) => action());

            var dispatcher = new Mock<IReceivedMessageDispatcher>();

            var sut = CreateSut(infraReceiver, dispatcher, recovery: recovery);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls: 1), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Ack), watchdog.Token);

            cts.Cancel();
            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop, "the loop must continue after an empty receive and cancel cleanly");
            await loop;

            receiveCalls.Value.Should().BeGreaterThan(1, "the first receive returned null and the loop must have pulled again");
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Ack);
            dispatcher.Verify(
                d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "only the non-null received message must spawn a worker");
        }

        // ------------------------------------------------------------------ (d) fault-resettable teardown admission: a thrown quiesce body lets a later teardown re-run

        [Fact]
        public async Task MustRetryTeardownWhenFirstQuiesceBodyThrowsThenSecondTeardownDisposes()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // Arm the fake so its FIRST infra teardown call (here, StopReceiver) throws once. The first StopReceiver's
            // quiesce body therefore faults at the infra-teardown seam (after cancel + drain), so admission must RESET
            // rather than latch terminally, letting a SECOND teardown re-run cleanup and actually dispose the infra.
            infraReceiver.ArmThrowOnceOnTeardown();

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    Interlocked.Increment(ref inFlight.Value);
                    try
                    {
                        await releaseGate.WaitAsync(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight.Value);
                    }
                });

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            // Release the gate so the first teardown's drain can quiesce the workers, then StopReceiver. The injected
            // throw-once surfaces at the infra-teardown seam, so StopReceiver must SURFACE the fault per its entrypoint
            // contract (it re-throws the quiesce fault to the caller).
            releaseGate.Release(messageCount);

            Func<Task> firstTeardown = () => sut.StopReceiver();
            await firstTeardown.Should().ThrowAsync<InvalidOperationException>(
                "the first quiesce body's infra-teardown threw, and StopReceiver surfaces that fault to its caller");

            Volatile.Read(ref inFlight.Value).Should().Be(0, "the first teardown still drained the in-flight workers before its infra-teardown faulted");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Stop, "the throwing StopReceiver call must not have recorded a successful Stop");

            // A SECOND teardown must win admission (the slot was reset, not latched), re-run the quiesce body, and dispose
            // the infra — proving the thrown quiesce did not permanently latch the receiver out of teardown.
            var second = sut.DisposeAsync().AsTask();
            var secondCompleted = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(15)));
            secondCompleted.Should().BeSameAs(second, "the second teardown must re-run cleanup and complete cleanly after the first faulted");
            await second;

            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "the retrying teardown must dispose the infrastructure receiver exactly once");
        }

        // ------------------------------------------------------------------ (e) STALE-LOSER: concurrent teardowns racing a faulting gate-holder strand no caller
        //
        // RACE-CLASS WITNESS #1 (stale single-flight loser). Under the former hand-rolled lock-free admission a losing
        // caller awaited the winner's swappable TaskCompletionSource; if the winner's quiesce body THREW, the loser could
        // observe a stranded/swapped TCS and hang. The SemaphoreSlim teardown gate makes that class structurally
        // impossible: every caller serializes on the gate, a thrown QuiesceCoreAsync leaves the terminal lifecycle UNSET
        // (no TCS to strand), and the gate's finally Release lets the NEXT caller re-run cleanly. Here two concurrent
        // teardowns race the first (faulting) admission; the watchdog turns any stranded-caller hang into a deterministic
        // Timeout, and a subsequent teardown must dispose the infra exactly once.
        [Fact]
        public async Task MustNotStrandConcurrentTeardownsWhenGateHolderQuiesceFaultsOnce()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // The FIRST infra teardown call throws once at the infra-teardown seam, so whichever caller acquires the gate
            // first faults its quiesce body. The gate's finally releases admission with the terminal lifecycle unset, so
            // the remaining racers (and a later teardown) must re-run cleanly rather than strand.
            infraReceiver.ArmThrowOnceOnTeardown();

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    Interlocked.Increment(ref inFlight.Value);
                    try
                    {
                        await releaseGate.WaitAsync(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight.Value);
                    }
                });

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            // Release the gate so the drains can quiesce the workers, then race THREE teardowns concurrently against the
            // single throw-once. Exactly one of them faults at the infra-teardown seam; the others either short-circuit on
            // the terminal lifecycle (if a sibling already latched it) or re-run cleanly. None may hang — the watchdog
            // converts a stranded-caller regression into a Timeout. Faults are captured per-task and asserted on after.
            releaseGate.Release(messageCount);

            async Task<Exception> Teardown(Func<Task> teardown)
            {
                try
                {
                    await teardown();
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
            }

            var racers = Task.WhenAll(
                Teardown(() => sut.StopReceiver()),
                Teardown(() => sut.StopReceiver()),
                Teardown(() => sut.DisposeAsync().AsTask()));

            var racersCompleted = await Task.WhenAny(racers, Task.Delay(TimeSpan.FromSeconds(15)));
            racersCompleted.Should().BeSameAs(racers, "no teardown may strand on a faulting gate-holder — a stranded caller would never complete");
            var faults = await racers;

            // Exactly one racer ran the faulting infra-teardown body (the throw-once), so exactly one surfaces the
            // simulated fault; the others short-circuited on the terminal lifecycle or re-ran cleanly without faulting.
            faults.Count(f => f is InvalidOperationException)
                .Should().Be(1, "only the single gate-holder that hit the throw-once may surface the simulated teardown fault");

            Volatile.Read(ref inFlight.Value).Should().Be(0, "all in-flight workers must have quiesced across the racing teardowns");

            // A subsequent teardown after the throw-once is consumed must dispose the infra exactly once, proving the
            // faulting admission did not permanently latch the receiver out of teardown (no stranded-TCS analogue).
            var settle = sut.DisposeAsync().AsTask();
            var settleCompleted = await Task.WhenAny(settle, Task.Delay(TimeSpan.FromSeconds(15)));
            settleCompleted.Should().BeSameAs(settle, "a later teardown must re-run cleanly after the faulting admission released the gate");
            await settle;

            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "the infrastructure receiver must end disposed exactly once despite the faulting gate-holder and racing teardowns");
        }
    }
}
