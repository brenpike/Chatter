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

        private static ReceiverOptions BuildReceiverOptions(int maxReceiveAttempts = 10)
            => new ReceiverOptions
            {
                InfrastructureType = InMemoryMessagingInfrastructureProvider.InfrastructureType,
                MessageReceiverPath = "test-queue",
                SendingPath = "test-queue",
                ErrorQueuePath = "error-queue",
                DeadLetterQueuePath = "deadletter-queue",
                TransactionMode = TransactionMode.None,
                MaxReceiveAttempts = maxReceiveAttempts,
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

            await infraReceiver.Drained;

            // Wait for the Ack to land in the log before cancelling, to avoid the race where
            // cancellation fires before ProcessMessageAsync runs and no disposition is recorded.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

            await infraReceiver.Drained;

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

            await infraReceiver.Drained;

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

            await infraReceiver.Drained;

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Deadletter, watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Deadletter);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Ack);
            maxReceivesAction.Verify(a => a.ExecuteAsync(It.IsAny<FailureContext>()), Times.Once);
        }
    }
}
