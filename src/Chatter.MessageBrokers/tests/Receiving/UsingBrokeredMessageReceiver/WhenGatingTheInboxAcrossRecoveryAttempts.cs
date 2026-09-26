#nullable disable

using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.MessageBrokers.Reliability.Inbox;
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
    public class WhenGatingTheInboxAcrossRecoveryAttempts : Testing.Core.Context
    {
        private class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }

        private class FakeCommand : ICommand { }

        /// <summary>
        /// A handler that constructs a fresh <see cref="FakeCommand"/> on every call and runs it through a real
        /// <see cref="InboxBehavior{TMessage}"/> on the delivery's context; its first call fails from inside the
        /// inbox, so the receiver's recovery strategy retries the delivery on the same context. The behavior logs through
        /// a <see cref="NullLogger{T}"/> because Castle cannot proxy <c>ILogger&lt;InboxBehavior&lt;FakeCommand&gt;&gt;</c>
        /// for the private <see cref="FakeCommand"/>.
        /// </summary>
        private sealed class FreshCommandHandler
        {
            private readonly InboxBehavior<FakeCommand> _inboxBehavior;
            private int _attemptCount;

            public FreshCommandHandler(IBrokeredMessageInbox inbox)
            {
                _inboxBehavior = new InboxBehavior<FakeCommand>(inbox, NullLogger<InboxBehavior<FakeCommand>>.Instance);
            }

            public List<FakeCommand> ConstructedCommands { get; } = new List<FakeCommand>();

            public Task HandleAttemptAsync(MessageBrokerContext messageContext)
            {
                _attemptCount++;
                var command = new FakeCommand();
                ConstructedCommands.Add(command);
                var failThisAttempt = _attemptCount == 1;
                return _inboxBehavior.Handle(command, messageContext, () => RunCommandHandlerAsync(failThisAttempt));
            }

            private static Task RunCommandHandlerAsync(bool failThisAttempt)
                => failThisAttempt
                    ? Task.FromException(new InvalidOperationException("transient failure on the first attempt"))
                    : Task.CompletedTask;
        }

        /// <summary>
        /// A receiver that overrides the dispatch to build its own command per attempt, the shape
        /// Chatter.SqlChangeFeed's ChangeFeedReceiver takes.
        /// </summary>
        private sealed class FreshCommandReceiver : BrokeredMessageReceiver<FakeMessage>
        {
            private readonly FreshCommandHandler _handler;

            public FreshCommandReceiver(IMessagingInfrastructureProvider infrastructureProvider, IRecoveryStrategy recoveryStrategy, FreshCommandHandler handler)
                : base(infrastructureProvider,
                       BuildBrokerOptions(),
                       NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                       new Mock<IMaxReceivesExceededAction>().Object,
                       new Mock<ICriticalFailureNotifier>().Object,
                       recoveryStrategy,
                       new Mock<IReceivedMessageDispatcher>().Object)
            {
                _handler = handler;
            }

            public override Task DispatchReceivedMessageAsync(FakeMessage payload, MessageBrokerContext messageContext, CancellationToken receiverTokenSource)
                => _handler.HandleAttemptAsync(messageContext);
        }

        private static MessageBrokerOptions BuildBrokerOptions()
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = TransactionMode.None;
            return opts;
        }

        private static ReceiverOptions BuildReceiverOptions()
            => new ReceiverOptions
            {
                InfrastructureType = InMemoryMessagingInfrastructureProvider.InfrastructureType,
                MessageReceiverPath = "test-queue",
                SendingPath = "test-queue",
                ErrorQueuePath = "error-queue",
                DeadLetterQueuePath = "deadletter-queue",
                TransactionMode = TransactionMode.None,
                MaxReceiveAttempts = 10,
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

        /// <summary>
        /// Builds the real retry-with-circuit-breaker strategy. Attempt 1 fails and attempt 2 succeeds, so
        /// MaxRetryAttempts=3 leaves ONE attempt of headroom (see WhenRecoveringThroughTheSeam's attempt-arithmetic
        /// note); the breaker never trips because its predicate list is empty.
        /// </summary>
        private static IRecoveryStrategy BuildStrategy()
        {
            var cbOptions = new CircuitBreakerOptions
            {
                OpenToHalfOpenWaitTimeInSeconds = 0,
                ConcurrentHalfOpenAttempts = 1,
                NumberOfFailuresBeforeOpen = 2,
                NumberOfHalfOpenSuccessesToClose = 1,
                SecondsOpenBeforeCriticalFailureNotification = 0,
            };
            var circuitBreaker = new CircuitBreaker(
                new InMemoryCircuitBreakerStateStore(NullLogger<InMemoryCircuitBreakerStateStore>.Instance),
                cbOptions,
                NullLogger<CircuitBreaker>.Instance,
                new CircuitBreakerExceptionEvaluator(Array.Empty<ICircuitBreakerExceptionPredicatesProvider>()));
            var recoveryOptions = new RecoveryOptions { MaxRetryAttempts = 3 };
            var retryEvaluator = new RetryExceptionEvaluator(
                new[] { new ConfigRetryExceptionPredicatesProvider(
                    new Predicate<Exception>[] { e => e is InvalidOperationException }) });
            var retryStrategy = new RetryStrategy(
                recoveryOptions,
                NullLogger<RetryStrategy>.Instance,
                new NoDelayRetry(),
                retryEvaluator);
            return new RetryWithCircuitBreakerStrategy(recoveryOptions, circuitBreaker, retryStrategy);
        }

        private static Mock<IBrokeredMessageInbox> BuildRecordingInbox(List<FakeCommand> receivedViaInbox)
        {
            var inbox = new Mock<IBrokeredMessageInbox>();
            inbox.Setup(i => i.ReceiveViaInbox(It.IsAny<FakeCommand>(), It.IsAny<IMessageBrokerContext>(), It.IsAny<Func<Task>>()))
                 .Callback<FakeCommand, IMessageBrokerContext, Func<Task>>((command, _, __) => receivedViaInbox.Add(command))
                 .Returns<FakeCommand, IMessageBrokerContext, Func<Task>>((_, __, messageReceiver) => messageReceiver());
            return inbox;
        }

        /// <summary>
        /// Awaits the drain, bounded by the watchdog so a receiver loop that never reaches ReceiveMessageAsync fails
        /// promptly instead of hanging (mirrors WhenRecoveringThroughTheSeam).
        /// </summary>
        private static async Task AwaitDrainedAsync(InMemoryMessagingInfrastructureReceiver infraReceiver, CancellationToken watchdog)
        {
            var watchdogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (watchdog.Register(() => watchdogTcs.TrySetCanceled(watchdog)))
            {
                var completed = await Task.WhenAny(infraReceiver.Drained, watchdogTcs.Task);
                await completed;
            }
        }

        private static async Task WaitForDispositionAsync(InMemoryMessagingInfrastructureReceiver infraReceiver, ReceiverCall expectedDisposition, CancellationToken watchdog)
        {
            while (!infraReceiver.CallLog.Contains(expectedDisposition))
            {
                watchdog.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private static async Task ReceiveUntilAckedAsync(BrokeredMessageReceiver<FakeMessage> sut, InMemoryMessagingInfrastructureReceiver infraReceiver)
        {
            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Ack, watchdog.Token);

            cts.Cancel();
            await loop;
        }

        [Fact]
        public async Task MustGateTheFirstCommandOfEveryRecoveryAttemptWhenTheHandlerBuildsAFreshOne()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext());
            var receivedViaInbox = new List<FakeCommand>();
            var handler = new FreshCommandHandler(BuildRecordingInbox(receivedViaInbox).Object);
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns<FakeMessage, MessageBrokerContext, CancellationToken>((_, messageContext, __) => handler.HandleAttemptAsync(messageContext));
            var sut = new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: new InMemoryMessagingInfrastructureProvider(infraReceiver),
                messageBrokerOptions: BuildBrokerOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: BuildStrategy(),
                receivedMessageDispatcher: dispatcher.Object);

            await ReceiveUntilAckedAsync(sut, infraReceiver);

            handler.ConstructedCommands.Should().HaveCount(2, because: "attempt 1 fails and the recovery strategy retries once");
            receivedViaInbox.Should().Equal(handler.ConstructedCommands,
                because: "each recovery attempt's first command is that attempt's Delivery Entry and must be received via the inbox");
        }

        [Fact]
        public async Task MustGateTheFirstCommandOfEveryAttemptWhenAReceiverOverridesTheDispatch()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext());
            var receivedViaInbox = new List<FakeCommand>();
            var handler = new FreshCommandHandler(BuildRecordingInbox(receivedViaInbox).Object);
            var sut = new FreshCommandReceiver(new InMemoryMessagingInfrastructureProvider(infraReceiver), BuildStrategy(), handler);

            await ReceiveUntilAckedAsync(sut, infraReceiver);

            handler.ConstructedCommands.Should().HaveCount(2, because: "attempt 1 fails and the recovery strategy retries once");
            receivedViaInbox.Should().Equal(handler.ConstructedCommands,
                because: "each recovery attempt's first command is that attempt's Delivery Entry and must be received via the inbox");
        }
    }
}
