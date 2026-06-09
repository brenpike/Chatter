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
    }
}
