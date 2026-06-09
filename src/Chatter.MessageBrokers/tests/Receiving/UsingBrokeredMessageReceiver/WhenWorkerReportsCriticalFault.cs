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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    // INVARIANT: a CriticalReceiverException raised inside a worker's DispatchAsync is published to the loop-observed
    // fault field, stops the loop, and routes through the notify-exactly-once epilogue. These tests prove the worker ->
    // loop fault hand-off (_workerCriticalFault + SignalLoopCriticalFault), the FailureContext shape handed to
    // ICriticalFailureNotifier.Notify, and — critically — that Notify fires BEFORE the worker drain (the #149 fail-fast
    // ordering), so an unbounded drain can never starve the host's critical-failure notification.
    public class WhenWorkerReportsCriticalFault : Testing.Core.Context
    {
        private static MessageBrokerOptions BuildOptions(TransactionMode mode = TransactionMode.None)
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = mode;
            return opts;
        }

        // INVARIANT: pass-through IRecoveryStrategy invokes the delegate exactly once, no retry machinery. A
        // CriticalReceiverException thrown by the dispatched action therefore propagates straight to the worker's
        // critical-fault catch rather than being absorbed by retry.
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
            Mock<ICriticalFailureNotifier> criticalNotifier)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);

            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: criticalNotifier.Object,
                recoveryStrategy: PassThroughRecovery().Object,
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

        // ------------------------------------------------------------------ (a) Notify fired once with the thrown exception + error-queue name, (b) loop stops

        [Fact]
        public async Task MustNotifyOnceWithCriticalFaultAndStopLoop()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext());

            var thrown = new CriticalReceiverException("boom");

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(thrown);

            FailureContext observedContext = null;
            var notified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var criticalNotifier = new Mock<ICriticalFailureNotifier>();
            criticalNotifier
                .Setup(n => n.Notify(It.IsAny<FailureContext>()))
                .Returns((FailureContext ctx) =>
                {
                    observedContext = ctx;
                    notified.TrySetResult(true);
                    return Task.CompletedTask;
                });

            var sut = CreateSut(infraReceiver, dispatcher, criticalNotifier);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls: 1), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var completed = await Task.WhenAny(notified.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(notified.Task, "the worker critical fault must drive ICriticalFailureNotifier.Notify");
            await notified.Task;

            // The loop must stop on the fault: awaiting the StartReceiver task completes (the fault is swallowed by the
            // when(IsReceiving) catch in StartReceiver, never propagated to the caller of a running loop).
            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop, "the loop must stop after the critical fault");
            await loop;

            criticalNotifier.Verify(n => n.Notify(It.IsAny<FailureContext>()), Times.Once,
                "the critical fault must notify exactly once");
            observedContext.Should().NotBeNull();
            observedContext.Failure.Should().BeSameAs(thrown, "the FailureContext must carry the thrown CriticalReceiverException");
            observedContext.ErrorQueueName.Should().Be("error-queue", "the FailureContext must carry the receiver's ErrorQueueName");
        }

        // ------------------------------------------------------------------ (c) notify-BEFORE-worker-drain (#149 fail-fast ordering)

        [Fact]
        public async Task MustNotifyBeforeDrainingSiblingWorkers()
        {
            const int maxConcurrentCalls = 2;
            const int messageCount = 2;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // One worker faults critically; the OTHER (sibling) worker blocks indefinitely on a gate that is released
            // ONLY from inside the Notify callback. If the implementation drained BEFORE notifying, the drain's
            // Task.WhenAll would block forever on the gated sibling, the gate would never open, and the 15s watchdog
            // would fire. The test passing therefore PROVES notify precedes drain. This is an ordering proof, NOT a
            // timestamp comparison.
            using var siblingReleaseGate = new SemaphoreSlim(0, 1);
            var siblingEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var faultArmed = new StrongBox<int>(0);
            var thrown = new CriticalReceiverException("boom");

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    // The FIRST dispatched message becomes the critical-faulting worker; the second becomes the gated
                    // sibling. Interlocked makes the assignment race-free regardless of which worker runs first. The
                    // faulter waits until the sibling has actually entered (and thus is in the in-flight set) BEFORE
                    // throwing, so the loop genuinely has a gated sibling to drain when it handles the fault.
                    if (Interlocked.Increment(ref faultArmed.Value) == 1)
                    {
                        await siblingEntered.Task;
                        throw thrown;
                    }

                    siblingEntered.TrySetResult(true);
                    // INVARIANT: wait WITHOUT the worker token so cancellation (SignalLoopCriticalFault cancels the loop
                    // token) can NOT unblock the sibling. The ONLY release is the Notify callback. If the impl drained
                    // before notifying, Task.WhenAll would block here forever and the watchdog fires — that is what makes
                    // this an ordering proof rather than a timestamp comparison.
                    await siblingReleaseGate.WaitAsync();
                });

            var criticalNotifier = new Mock<ICriticalFailureNotifier>();
            criticalNotifier
                .Setup(n => n.Notify(It.IsAny<FailureContext>()))
                .Returns((FailureContext _) =>
                {
                    // Release the gated sibling EXCLUSIVELY here. The drain that runs AFTER this notify can then
                    // complete; if notify ran after drain, the drain would already be stuck on the gated sibling.
                    siblingReleaseGate.Release();
                    return Task.CompletedTask;
                });

            var sut = CreateSut(infraReceiver, dispatcher, criticalNotifier);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Ensure the sibling is actually in-flight (so the drain has something to wait on) before relying on the
            // ordering. The watchdog converts a never-entered sibling into a prompt failure.
            var siblingCompleted = await Task.WhenAny(siblingEntered.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            siblingCompleted.Should().BeSameAs(siblingEntered.Task, "the sibling worker must enter DispatchAsync and gate in-flight");

            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop,
                "the loop must notify BEFORE draining; draining first would deadlock on the gated sibling and never complete");
            await loop;

            criticalNotifier.Verify(n => n.Notify(It.IsAny<FailureContext>()), Times.Once);
        }

        // ------------------------------------------------------------------ (d) fault published while loop parked in WaitAsync still wakes the loop

        [Fact]
        public async Task MustWakeLoopParkedInWaitAsyncWhenFaultPublishedWithSlotsFull()
        {
            const int maxConcurrentCalls = 1;
            const int messageCount = 1;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // With MaxConcurrentCalls == 1, once the single worker is admitted the loop's next WaitAsync parks (the only
            // slot is taken). The worker then faults; SignalLoopCriticalFault cancels the loop token, which is what wakes
            // the parked WaitAsync. The fault still routes to Notify exactly once. The watchdog catches a regression
            // where the parked loop never wakes.
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFault = new SemaphoreSlim(0, 1);
            var thrown = new CriticalReceiverException("boom");

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    entered.TrySetResult(true);
                    // Hold the worker (and thus the only slot) so the loop parks in its next WaitAsync, then fault.
                    await releaseFault.WaitAsync(token);
                    throw thrown;
                });

            var notified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var criticalNotifier = new Mock<ICriticalFailureNotifier>();
            criticalNotifier
                .Setup(n => n.Notify(It.IsAny<FailureContext>()))
                .Returns((FailureContext _) =>
                {
                    notified.TrySetResult(true);
                    return Task.CompletedTask;
                });

            var sut = CreateSut(infraReceiver, dispatcher, criticalNotifier);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var enteredCompleted = await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            enteredCompleted.Should().BeSameAs(entered.Task, "the worker must occupy the only slot so the loop parks in WaitAsync");

            // Give the loop a chance to reach its parked WaitAsync, then release the worker so it faults while the loop
            // is parked.
            await Task.Yield();
            releaseFault.Release();

            var notifiedCompleted = await Task.WhenAny(notified.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            notifiedCompleted.Should().BeSameAs(notified.Task,
                "a fault published while the loop is parked in WaitAsync must still wake the loop and route to Notify");
            await notified.Task;

            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop, "the loop must complete after the parked-loop fault is handled");
            await loop;

            criticalNotifier.Verify(n => n.Notify(It.IsAny<FailureContext>()), Times.Once);
        }
    }
}
