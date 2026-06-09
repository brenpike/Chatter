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
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    public class WhenReceiving : Testing.Core.Context
    {
        // INVARIANT: MessageBrokerOptions.TransactionMode has internal set; accessible via InternalsVisibleTo("Chatter.MessageBrokers.Tests").
        private static MessageBrokerOptions BuildOptions(TransactionMode mode = TransactionMode.None)
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = mode;
            return opts;
        }

        // INVARIANT: pass-through IRecoveryStrategy invokes the delegate exactly once, no retry machinery.
        // The real RetryWithCircuitBreakerStrategy transitions are reserved for STEP-003.
        // All TResult shapes used by the receiver loop must be covered:
        //   Func<Task<MessageBrokerContext>> — ReceiveMessageAsync
        //   Func<Task<bool>>                — Ack/Nack/Deadletter/DispatchReceivedMessage/FailedRecoveryAction
        //   Func<Task<int>>                 — MessageDeliveryCountAsync
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

        // INVARIANT: body must deserialise cleanly as FakeMessage for non-poison tests.
        private static MessageBrokerContext BuildContext(byte[] body = null)
        {
            var converter = new JsonBodyConverter();
            body ??= converter.Convert(new FakeMessage { Value = "hello" });
            return new MessageBrokerContext(
                messageId: Guid.NewGuid().ToString(),
                body: body,
                applicationProperties: new Dictionary<string, object>(),
                messageReceiverPath: "test-queue",
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: converter);
        }

        // INVARIANT: Drained completes only if the receiver loop reaches ReceiveMessageAsync.
        // If the receiver faults during startup/initialization, StartReceiver catches and returns
        // without faulting the loop task, so a bare `await infraReceiver.Drained` would block forever
        // and hang the test run. Bound the wait on the same watchdog used for the disposition wait so
        // an unreached drain fails promptly instead of hanging.
        private static async Task AwaitDrainedAsync(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            CancellationToken watchdog)
        {
            var watchdogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (watchdog.Register(() => watchdogTcs.TrySetCanceled(watchdog)))
            {
                var completed = await Task.WhenAny(infraReceiver.Drained, watchdogTcs.Task);
                await completed; // surface OperationCanceledException if the watchdog fired first
            }
        }

        // INVARIANT: Drained fires at message dequeue, before ProcessMessageAsync runs.
        // Asserting on the CallLog requires waiting until the expected disposition (Ack/Nack/Deadletter)
        // actually appears — not just until the message is dequeued. This helper yields until the
        // disposition call lands in the log, then signals the caller via the returned TCS.
        // The watchdog CTS bounds the wait to prevent an infinite spin on unexpected code paths.
        private static async Task WaitForDispositionAsync(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            ReceiverCall expectedDisposition,
            CancellationToken watchdog)
        {
            while (!infraReceiver.CallLog.Contains(expectedDisposition))
            {
                watchdog.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private BrokeredMessageReceiver<FakeMessage> CreateSut(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            Mock<IReceivedMessageDispatcher> dispatcher,
            Mock<IMaxReceivesExceededAction> maxReceivesAction = null,
            Mock<ICriticalFailureNotifier> criticalNotifier = null,
            Mock<IRecoveryStrategy> recovery = null)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);
            maxReceivesAction ??= new Mock<IMaxReceivesExceededAction>();
            criticalNotifier ??= new Mock<ICriticalFailureNotifier>();
            recovery ??= PassThroughRecovery();

            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: maxReceivesAction.Object,
                criticalFailureNotifier: criticalNotifier.Object,
                recoveryStrategy: recovery.Object,
                receivedMessageDispatcher: dispatcher.Object);
        }

        private class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }

        // ------------------------------------------------------------------ (a) successful receive → Ack

        [Fact]
        public async Task MustAckAfterSuccessfulHandlerDispatch()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            // Wait for the Ack to land in the log before cancelling, to avoid the race where
            // cancellation fires before ProcessMessageAsync runs and no disposition is recorded.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Ack, watchdog.Token);

            cts.Cancel();
            await loop;

            dispatcher.Verify(
                d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()),
                Times.Once);
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Ack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter);
        }

        // ------------------------------------------------------------------ (b) handler failure + DeliveryCount < Max → Nack

        [Fact]
        public async Task MustNackWhenHandlerThrowsAndDeliveryCountBelowMax()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.DeliveryCount = 1; // below MaxReceiveAttempts=10

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("handler boom"));

            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxReceiveAttempts: 10), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Nack, watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Ack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter);
        }

        // ------------------------------------------------------------------ (c) poisoned message (deserialization throws) → Deadletter

        [Fact]
        public async Task MustDeadletterPoisonedMessage()
        {
            // Body is not valid JSON for FakeMessage; GetMessageFromBody<FakeMessage> throws → PoisonedMessageException.
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            var badBody = new JsonBodyConverter().GetBytes("not-valid-json-object");
            infraReceiver.Enqueue(BuildContext(body: badBody));

            var dispatcher = new Mock<IReceivedMessageDispatcher>();

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Deadletter, watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Deadletter);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Ack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack);
            dispatcher.Verify(
                d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ------------------------------------------------------------------ (d) handler failure + DeliveryCount >= Max → Deadletter + MaxReceivesExceededAction

        [Fact]
        public async Task MustDeadletterAndInvokeMaxReceivesActionWhenDeliveryCountExceedsMax()
        {
            const int maxAttempts = 3;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.DeliveryCount = maxAttempts; // equals Max → triggers exceeded path

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

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Deadletter, watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Deadletter);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Ack);
            maxReceivesAction.Verify(a => a.ExecuteAsync(It.IsAny<FailureContext>()), Times.Once);
        }

        // ------------------------------------------------------------------ (MaxConcurrentCalls) honored at receiver init

        [Fact]
        public async Task MustReceiveSuccessfullyWhenMaxConcurrentCallsAboveDefault()
        {
            // StartReceiverImpl reads ReceiverOptions.MaxConcurrentCalls into the concurrency semaphore at init
            // (_concurrentMessagesSemaphore = new SemaphoreSlim(maxConcurrentCalls, maxConcurrentCalls)). A value
            // above the default 1 must be accepted and leave the receive→dispatch→Ack path working — proving the
            // new option is wired through receiver startup without regressing the loop. (The single receive loop
            // is sequential, so concurrency throughput is not observable through the in-memory double; this pins
            // that a >1 value is honored at init rather than rejected.)
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls: 4), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Ack, watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Ack);
            dispatcher.Verify(
                d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ------------------------------------------------------------------ (startup signal) internal seam + public-surface guard

        // INVARIANT: the go-live startup signal lives on the INTERNAL IReceiverStartupSignal seam (reachable here
        // via InternalsVisibleTo("Chatter.MessageBrokers.Tests")), NOT the public IReceiveMessages contract. The
        // concrete receiver produces it; BrokeredMessageReceiverBackgroundService is its only consumer.
        [Fact]
        public async Task MustCompleteReceivingStartedSignalOnGoLive()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher);

            sut.Should().BeAssignableTo<IReceiverStartupSignal>(
                "the go-live signal lives on the internal IReceiverStartupSignal seam");
            var startupSignal = (IReceiverStartupSignal)sut;
            startupSignal.ReceivingStarted.IsCompleted.Should().BeFalse("the receiver has not started yet");

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            // Drained fires once the receive loop is live; go-live (IsReceiving = true) is set on the same path
            // immediately before the loop is awaited, so ReceivingStarted has completed by the time it drains.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Ack, watchdog.Token);

            sut.IsReceiving.Should().BeTrue();
            startupSignal.ReceivingStarted.IsCompletedSuccessfully
                .Should().BeTrue("the signal completes exactly when the receiver goes live");

            cts.Cancel();
            await loop;
        }

        // INVARIANT: public-API-shape guard. ReceivingStarted must NOT be a member of the public IReceiveMessages
        // contract — it was reverted off the public surface to keep 0.12.0 a non-breaking MINOR. This guard fails
        // if the breaking member is ever silently re-added to the public interface.
        [Fact]
        public void MustNotExposeReceivingStartedOnPublicReceiveMessagesContract()
        {
            typeof(IReceiveMessages).GetProperty("ReceivingStarted")
                .Should().BeNull("ReceivingStarted lives on the internal IReceiverStartupSignal seam, not the public contract");
        }
    }
}
