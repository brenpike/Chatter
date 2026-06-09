#nullable disable

using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
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
    // INVARIANT: drives the per-worker generic-error recovery ladder in ProcessReceivedMessageWorkerAsync under the
    // SNAPSHOTTED worker token. Covers the Nack (DeliveryCount < Max) and Deadletter + MaxReceivesExceededAction
    // (DeliveryCount >= Max) branches, the settlement-probe failure that must be swallowed and critically logged
    // (without leaking a slot or faulting the loop), and the cancellation-during-recovery swallow. NullLogger makes
    // the critical-log no-op; gating primitives + 15s watchdog turn any leaked-slot/hang regression into a Timeout.
    public class WhenWorkerErrorLadderRuns : Testing.Core.Context
    {
        private static MessageBrokerOptions BuildOptions(TransactionMode mode = TransactionMode.None)
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = mode;
            return opts;
        }

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

        private static ReceiverOptions BuildReceiverOptions(int maxReceiveAttempts = 10, int maxConcurrentCalls = 1)
            => new ReceiverOptions
            {
                InfrastructureType = InMemoryMessagingInfrastructureProvider.InfrastructureType,
                MessageReceiverPath = "test-queue",
                SendingPath = "test-queue",
                ErrorQueuePath = "error-queue",
                DeadLetterQueuePath = "deadletter-queue",
                TransactionMode = TransactionMode.None,
                MaxReceiveAttempts = maxReceiveAttempts,
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
            Mock<IMaxReceivesExceededAction> maxReceivesAction = null,
            Mock<IRecoveryStrategy> recovery = null)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);
            maxReceivesAction ??= new Mock<IMaxReceivesExceededAction>();
            recovery ??= PassThroughRecovery();

            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: maxReceivesAction.Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
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

        // ------------------------------------------------------------------ (a) generic handler error + DeliveryCount < Max → Nack

        [Fact]
        public async Task MustNackWhenHandlerThrowsGenericAndDeliveryCountBelowMax()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.DeliveryCount = 1; // below MaxReceiveAttempts

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("handler boom"));

            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxReceiveAttempts: 10), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Nack), watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Ack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter);
        }

        // ------------------------------------------------------------------ (b) generic handler error + DeliveryCount >= Max → Deadletter + MaxReceivesExceededAction

        [Fact]
        public async Task MustDeadletterAndInvokeMaxReceivesActionWhenDeliveryCountAtMax()
        {
            const int maxAttempts = 3;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.DeliveryCount = maxAttempts; // >= Max

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("handler boom at max"));

            var maxReceivesAction = new Mock<IMaxReceivesExceededAction>();
            maxReceivesAction
                .Setup(a => a.ExecuteAsync(It.IsAny<FailureContext>()))
                .Returns(Task.CompletedTask);

            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher, maxReceivesAction: maxReceivesAction);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxReceiveAttempts: maxAttempts), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Deadletter), watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Deadletter);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Ack);
            maxReceivesAction.Verify(a => a.ExecuteAsync(It.IsAny<FailureContext>()), Times.Once);
        }

        // ------------------------------------------------------------------ (c) settlement-probe failure → swallowed + critically logged, loop unaffected

        [Fact]
        public async Task MustSwallowSettlementProbeFailureWithoutLeakingSlotOrFaultingLoop()
        {
            const int messageCount = 2;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            infraReceiver.DeliveryCount = 1;

            // The FIRST message's handler throws generic, then the settlement probe (MessageDeliveryCountAsync via the
            // Func<Task<int>> recovery overload) throws an unexpected fault → the worker's catch(Exception recoveryError)
            // swallows it and logs critically. The loop must be unaffected and not leak the slot: the SECOND message
            // must still be received and acked. The first message's handler must NOT throw on the second message.
            var firstHandlerThrew = new StrongBox<int>(0);
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns((FakeMessage _, MessageBrokerContext __, CancellationToken ___) =>
                {
                    if (Interlocked.Increment(ref firstHandlerThrew.Value) == 1)
                    {
                        throw new InvalidOperationException("handler boom");
                    }
                    return Task.CompletedTask;
                });

            // Recovery: receive + ack/nack delegate through; the int (delivery-count probe) overload throws ONCE for the
            // first message's settlement probe, then delegates through for any later use.
            var probeCalls = new StrongBox<int>(0);
            var recovery = new Mock<IRecoveryStrategy>();
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>((action, _) =>
                {
                    if (Interlocked.Increment(ref probeCalls.Value) == 1)
                    {
                        throw new Exception("settlement probe failed");
                    }
                    return action();
                });

            infraReceiver.Enqueue(BuildContext());
            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher, recovery: recovery);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxReceiveAttempts: 10), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // The second message must be acked despite the first message's settlement-probe failure, proving the slot
            // was not leaked and the loop kept running.
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Ack), watchdog.Token);

            cts.Cancel();
            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop, "the loop must be unaffected by the swallowed settlement-probe failure and cancel cleanly");
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Ack, "a later message must still process, proving the slot was not leaked");
            probeCalls.Value.Should().BeGreaterThan(0, "the settlement probe must have been invoked and thrown");
        }

        // ------------------------------------------------------------------ (d) cancellation during recovery swallowed under the snapshotted token

        [Fact]
        public async Task MustSwallowCancellationDuringRecoveryUnderSnapshottedToken()
        {
            const int messageCount = 1;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            infraReceiver.DeliveryCount = 1;

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("handler boom"));

            // The worker enters its recovery ladder and awaits the delivery-count probe (Func<Task<int>>). Hold that
            // probe on a gate that honors the worker token, signalling that the worker is mid-recovery. The test then
            // cancels the loop token (linked to the worker token); the gated probe throws OCE which the worker's
            // recovery catch swallows under workerToken.IsCancellationRequested. No fault must escape; the loop completes.
            var midRecovery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var probeGate = new SemaphoreSlim(0, 1);

            var recovery = new Mock<IRecoveryStrategy>();
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>(async (action, token) =>
                {
                    midRecovery.TrySetResult(true);
                    // Honor the worker token so cancellation unwinds the probe with an OperationCanceledException.
                    await probeGate.WaitAsync(token);
                    return await action();
                });

            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher, recovery: recovery);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxReceiveAttempts: 10), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var midCompleted = await Task.WhenAny(midRecovery.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            midCompleted.Should().BeSameAs(midRecovery.Task, "the worker must reach the recovery probe and gate mid-recovery");

            // Cancel while the worker is mid-recovery: the linked worker token cancels the gated probe, whose OCE the
            // worker recovery catch swallows.
            cts.Cancel();

            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop, "cancellation during recovery must be swallowed and the loop must complete without faulting");
            await loop; // must not throw

            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Ack, "the handler threw, so no Ack is expected");
        }
    }
}
