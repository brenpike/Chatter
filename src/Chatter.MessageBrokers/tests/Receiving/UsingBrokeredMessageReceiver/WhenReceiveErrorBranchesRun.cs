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
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    // INVARIANT: drives two genuinely-uncovered ERROR branches in BrokeredMessageReceiver that the sibling receiver tests
    // do not reach:
    //   (1) a CriticalReceiverException thrown by the RECEIVE call itself (the recovery Func<Task<MessageBrokerContext>>
    //       overload throws critical) is caught at the inline receive catch(CriticalReceiverException), releases the slot,
    //       stops the loop, and fires ICriticalFailureNotifier.Notify exactly once — distinct from the worker-published
    //       critical-fault tests, where the fault originates in DispatchAsync.
    //   (2) delivery count >= max + the deadletter recovery call returns false (the recovery Func<Task<bool>> overload
    //       throws so TryDeadletterWithRecoveryAsync returns false) means IMaxReceivesExceededAction.ExecuteAsync is NOT
    //       invoked (the `if (await TryDeadletter...)` false branch).
    // Reuses the established harness: InMemoryMessagingInfrastructureReceiver fake + IRecoveryStrategy mock + 15s watchdog,
    // NullLogger to no-op the critical logs. A regression becomes a Timeout rather than a hang.
    public class WhenReceiveErrorBranchesRun : Testing.Core.Context
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
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<SettlementResult>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<SettlementResult>>, CancellationToken>((action, _) => action());
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
            Mock<IRecoveryStrategy> recovery,
            Mock<IMaxReceivesExceededAction> maxReceivesAction = null,
            Mock<ICriticalFailureNotifier> criticalNotifier = null)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);
            maxReceivesAction ??= new Mock<IMaxReceivesExceededAction>();
            criticalNotifier ??= new Mock<ICriticalFailureNotifier>();

            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: maxReceivesAction.Object,
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

        // ------------------------------------------------------------------ (1) CriticalReceiverException from the RECEIVE call itself

        [Fact]
        public async Task MustNotifyOnceAndStopLoopWhenReceiveItselfThrowsCritical()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext());

            var thrown = new CriticalReceiverException("receive boom");

            // The recovery MessageBrokerContext overload (the RECEIVE call) throws critical on its FIRST invocation. This
            // hits the inline receive catch(CriticalReceiverException) in the loop — distinct from the worker-published
            // path where DispatchAsync throws. The inline catch releases the slot and rethrows to stop the loop; the outer
            // loop catch records the fault and the notify-once epilogue fires ICriticalFailureNotifier.Notify exactly once.
            var recovery = new Mock<IRecoveryStrategy>();
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((_, __) => throw thrown);
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<SettlementResult>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<SettlementResult>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>((action, _) => action());

            var dispatcher = new Mock<IReceivedMessageDispatcher>();

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

            var sut = CreateSut(infraReceiver, dispatcher, recovery, criticalNotifier: criticalNotifier);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var notifiedCompleted = await Task.WhenAny(notified.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            notifiedCompleted.Should().BeSameAs(notified.Task, "a critical fault from the receive call itself must drive ICriticalFailureNotifier.Notify");
            await notified.Task;

            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop, "the loop must stop after the inline receive critical fault");
            await loop;

            criticalNotifier.Verify(n => n.Notify(It.IsAny<FailureContext>()), Times.Once,
                "the inline-receive critical fault must notify exactly once");
            observedContext.Should().NotBeNull();
            observedContext.Failure.Should().BeSameAs(thrown, "the FailureContext must carry the CriticalReceiverException thrown by the receive call");
            dispatcher.Verify(
                d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "the receive itself faulted critically, so no message must reach the dispatcher");
        }

        // ------------------------------------------------------------------ (2) DeliveryCount >= max + deadletter reports Failed → MaxReceivesExceededAction NOT invoked

        [Fact]
        public async Task MustNotInvokeMaxReceivesActionWhenDeadletterRecoveryFailsAtMax()
        {
            const int maxAttempts = 3;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.DeliveryCount = maxAttempts; // >= Max

            // The handler throws generic so the worker enters its delivery-count ladder. DeliveryCount >= Max routes to
            // TryDeadletterWithRecoveryAsync, whose recovery Func<Task<SettlementResult>> overload THROWS —
            // TryDeadletterWithRecoveryAsync catches and reports Failed, so the not-settled branch SKIPS IMaxReceivesExceededAction.
            var handlerThrew = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    handlerThrew.TrySetResult(true);
                    throw new InvalidOperationException("handler boom at max");
                });

            // The bool overload carries the dispatch action ONLY, and delegates through so the dispatcher's own throw routes
            // the worker to its generic catch. The SettlementResult overload carries the deadletter call and THROWS, so
            // TryDeadletterWithRecoveryAsync catches and reports Failed. The int (delivery-count) overload delegates through
            // so the probe returns DeliveryCount (>= max).
            var deadletterAttempted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovery = new Mock<IRecoveryStrategy>();
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<SettlementResult>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<SettlementResult>>, CancellationToken>((_, __) =>
                {
                    deadletterAttempted.TrySetResult(true);
                    throw new Exception("deadletter recovery failed");
                });
            recovery
                .Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>((action, _) => action());

            var maxReceivesAction = new Mock<IMaxReceivesExceededAction>();
            maxReceivesAction
                .Setup(a => a.ExecuteAsync(It.IsAny<FailureContext>()))
                .Returns(Task.CompletedTask);

            infraReceiver.Enqueue(BuildContext());

            var sut = CreateSut(infraReceiver, dispatcher, recovery, maxReceivesAction: maxReceivesAction);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxReceiveAttempts: maxAttempts), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var handlerCompleted = await Task.WhenAny(handlerThrew.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            handlerCompleted.Should().BeSameAs(handlerThrew.Task, "the handler must run and throw so the worker enters its delivery-count ladder");
            await handlerThrew.Task;

            var deadletterCompleted = await Task.WhenAny(deadletterAttempted.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            deadletterCompleted.Should().BeSameAs(deadletterAttempted.Task, "the worker must attempt the deadletter recovery (which throws → reports Failed)");
            await deadletterAttempted.Task;

            cts.Cancel();
            var loopCompleted = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            loopCompleted.Should().BeSameAs(loop, "the loop must cancel cleanly after the failed-deadletter branch");
            await loop;

            maxReceivesAction.Verify(a => a.ExecuteAsync(It.IsAny<FailureContext>()), Times.Never,
                "a failed deadletter (recovery threw → Failed) must SKIP the MaxReceivesExceededAction");
        }
    }
}
