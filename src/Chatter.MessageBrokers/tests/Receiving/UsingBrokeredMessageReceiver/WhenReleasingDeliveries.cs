#nullable disable

using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    // INVARIANT: pins the OPTIONAL IDeliveryReleaseSignal capability the receiver discovers by type check on the
    // messaging-infrastructure receiver. The signal fires exactly once per delivered message, AFTER that delivery's
    // settlement answer and BEFORE the concurrency slot is returned, and a throw from it is swallowed (never leaking
    // the slot). An infrastructure receiver that does not declare the interface is never called for it.
    public class WhenReleasingDeliveries : Testing.Core.Context
    {
        // INVARIANT: MessageBrokerOptions.TransactionMode has internal set; accessible via InternalsVisibleTo("Chatter.MessageBrokers.Tests").
        private static MessageBrokerOptions BuildOptions()
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = TransactionMode.None;
            return opts;
        }

        // INVARIANT: pass-through IRecoveryStrategy invokes the delegate exactly once, no retry machinery, so the
        // ordering observed is the receiver's and not the strategy's.
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

        // INVARIANT: body must deserialise cleanly as FakeMessage for every non-poison test.
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

        // INVARIANT: spins until the expected number of calls land in the locked CallLog snapshot. The watchdog bounds
        // the wait so a regression that never signals fails fast instead of hanging the run.
        private static async Task WaitForCallCountAsync(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            ReceiverCall call,
            int expectedCount,
            CancellationToken watchdog)
        {
            while (infraReceiver.CallLog.Count(logged => logged == call) < expectedCount)
            {
                watchdog.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private static BrokeredMessageReceiver<FakeMessage> CreateSut(
            IMessagingInfrastructureProvider provider,
            Mock<IReceivedMessageDispatcher> dispatcher,
            ILogger<BrokeredMessageReceiver<FakeMessage>> logger = null)
            => new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: logger ?? NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: PassThroughRecovery().Object,
                receivedMessageDispatcher: dispatcher.Object);

        private static void VerifyErrorLogged(
            Mock<ILogger<BrokeredMessageReceiver<FakeMessage>>> logger,
            Exception expected,
            Times times)
            => logger.Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.Is<Exception>(logged => ReferenceEquals(logged, expected)),
                    (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()),
                times);

        // PUBLIC, unlike the private nested doubles in the sibling suites: Moq must generate a proxy for
        // ILogger<BrokeredMessageReceiver<FakeMessage>>, and Castle cannot proxy a type whose generic argument is
        // inaccessible to the strong-named dynamic proxy assembly.
        public class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }

        // ------------------------------------------------------------------ (a) ordering: signal precedes slot release

        [Fact]
        public async Task MustSignalDeliveryReleasedBeforeReturningTheConcurrencySlot()
        {
            const int messageCount = 2;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // Park the FIRST delivery inside the hook. With MaxConcurrentCalls == 1 the loop cannot receive again until
            // the slot is released, so while the hook is parked the log must hold EXACTLY ONE Receive. Were the hook
            // called after the release, the loop would already have taken the second message.
            var hookEntered = infraReceiver.ArmDeliveryReleasedGate();

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(new InMemoryMessagingInfrastructureProvider(infraReceiver), dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls: 1), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var entered = await Task.WhenAny(hookEntered, Task.Delay(TimeSpan.FromSeconds(15), watchdog.Token));
            entered.Should().BeSameAs(hookEntered, "the delivery-release signal must fire for the first delivery");

            var logWhileParked = infraReceiver.CallLog;
            logWhileParked.Count(c => c == ReceiverCall.Receive).Should().Be(1,
                because: "the signal must run BEFORE the concurrency slot is released, so no second delivery can have been received yet");
            logWhileParked.Count(c => c == ReceiverCall.Ack).Should().Be(1,
                because: "the signal must run AFTER the settlement answer for its delivery");

            infraReceiver.ReleaseDeliveryReleasedGate();

            await WaitForCallCountAsync(infraReceiver, ReceiverCall.Receive, 2, watchdog.Token);
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.DeliveryReleased, 2, watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Count(c => c == ReceiverCall.DeliveryReleased).Should().Be(messageCount,
                because: "every delivered message signals its release exactly once");
        }

        // ------------------------------------------------------------------ (b) acknowledge path

        [Fact]
        public async Task MustSignalOnceWhenDeliveryIsAcknowledged()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext());

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(new InMemoryMessagingInfrastructureProvider(infraReceiver), dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.DeliveryReleased, 1, watchdog.Token);

            cts.Cancel();
            await loop;

            var callLog = infraReceiver.CallLog;
            callLog.Count(c => c == ReceiverCall.Ack).Should().Be(1);
            callLog.Count(c => c == ReceiverCall.DeliveryReleased).Should().Be(1,
                because: "an acknowledged delivery signals its release exactly once");
        }

        // ------------------------------------------------------------------ (c) acknowledge-failure path

        [Fact]
        public async Task MustSignalOnceWhenAcknowledgementFails()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.ArmAckFailure(new InvalidOperationException("The acknowledgement failed deliberately."));
            infraReceiver.Enqueue(BuildContext());

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(new InMemoryMessagingInfrastructureProvider(infraReceiver), dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.DeliveryReleased, 1, watchdog.Token);

            cts.Cancel();
            await loop;

            var callLog = infraReceiver.CallLog;
            callLog.Count(c => c == ReceiverCall.Ack).Should().Be(1);
            callLog.Count(c => c == ReceiverCall.DeliveryReleased).Should().Be(1,
                because: "a delivery whose acknowledgement failed still signals its release exactly once");
        }

        // ------------------------------------------------------------------ (d) negative-acknowledge path

        [Fact]
        public async Task MustSignalOnceWhenDeliveryIsNegativelyAcknowledged()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.DeliveryCount = 1; // below MaxReceiveAttempts, so the handler fault nacks rather than deadletters
            infraReceiver.Enqueue(BuildContext());

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("handler boom"));

            var sut = CreateSut(new InMemoryMessagingInfrastructureProvider(infraReceiver), dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.DeliveryReleased, 1, watchdog.Token);

            cts.Cancel();
            await loop;

            var callLog = infraReceiver.CallLog;
            callLog.Count(c => c == ReceiverCall.Nack).Should().Be(1);
            callLog.Count(c => c == ReceiverCall.DeliveryReleased).Should().Be(1,
                because: "a negatively acknowledged delivery signals its release exactly once");
        }

        // ------------------------------------------------------------------ (e) poison / deadletter path

        [Fact]
        public async Task MustSignalOnceWhenPoisonedDeliveryIsDeadlettered()
        {
            // Body is not valid JSON for FakeMessage; the body read throws -> PoisonedMessageException -> deadletter.
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext(body: new JsonBodyConverter().GetBytes("not-valid-json-object")));

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(new InMemoryMessagingInfrastructureProvider(infraReceiver), dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.DeliveryReleased, 1, watchdog.Token);

            cts.Cancel();
            await loop;

            var callLog = infraReceiver.CallLog;
            callLog.Count(c => c == ReceiverCall.Deadletter).Should().Be(1);
            callLog.Count(c => c == ReceiverCall.DeliveryReleased).Should().Be(1,
                because: "a deadlettered poison delivery signals its release exactly once");
        }

        // ------------------------------------------------------------------ (f) a throwing signal is logged and swallowed

        [Fact]
        public async Task MustLogAndSwallowASignalThatThrows()
        {
            const int messageCount = 2;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            var hookFailure = new InvalidOperationException("The delivery-release signal failed deliberately.");
            infraReceiver.ArmDeliveryReleasedFailure(hookFailure);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var logger = new Mock<ILogger<BrokeredMessageReceiver<FakeMessage>>>();
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(new InMemoryMessagingInfrastructureProvider(infraReceiver), dispatcher, logger.Object);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls: 1), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // The SECOND message is only ever received if the first delivery's slot was returned despite the throw.
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.Ack, messageCount, watchdog.Token);
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.DeliveryReleased, messageCount, watchdog.Token);

            cts.Cancel();
            await loop;

            // One error per failing signal: the throw is reported, never propagated.
            VerifyErrorLogged(logger, hookFailure, Times.Exactly(messageCount));
        }

        // ------------------------------------------------------------------ (g) ObjectDisposedException is swallowed silently

        [Fact]
        public async Task MustSwallowAnObjectDisposedExceptionFromTheSignalWithoutLoggingAnError()
        {
            const int messageCount = 2;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            var disposed = new ObjectDisposedException("infrastructure-receiver");
            infraReceiver.ArmDeliveryReleasedFailure(disposed);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var logger = new Mock<ILogger<BrokeredMessageReceiver<FakeMessage>>>();
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(new InMemoryMessagingInfrastructureProvider(infraReceiver), dispatcher, logger.Object);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls: 1), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.Ack, messageCount, watchdog.Token);
            await WaitForCallCountAsync(infraReceiver, ReceiverCall.DeliveryReleased, messageCount, watchdog.Token);

            cts.Cancel();
            await loop;

            // A teardown race is benign: swallowed like the concurrency-slot release's own ObjectDisposedException.
            logger.Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()),
                Times.Never);
        }

        // ------------------------------------------------------------------ (h) a receiver that omits the capability

        [Fact]
        public async Task MustNotSignalAReceiverThatDoesNotDeclareTheCapability()
        {
            var infraReceiver = new SignalUnawareInfrastructureReceiver(BuildContext());
            infraReceiver.Should().NotBeAssignableTo<IDeliveryReleaseSignal>(
                because: "the capability is optional and this double deliberately omits it");

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(new InMemoryMessagingInfrastructureProvider(infraReceiver), dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var acked = await Task.WhenAny(infraReceiver.Acked, Task.Delay(TimeSpan.FromSeconds(15), watchdog.Token));
            acked.Should().BeSameAs(infraReceiver.Acked, "a receiver without the capability must still process its delivery normally");

            cts.Cancel();
            await loop; // surfaces any fault the missing capability would have caused

            infraReceiver.AckCount.Should().Be(1);
        }

        // INVARIANT: implements ONLY IMessagingInfrastructureReceiver, so the receiver's type check must find no
        // delivery-release capability and call nothing. Fed through the provider's interface-typed constructor.
        private sealed class SignalUnawareInfrastructureReceiver : IMessagingInfrastructureReceiver
        {
            private readonly Queue<MessageBrokerContext> _messages;
            private readonly TaskCompletionSource<bool> _ackedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _ackCount;

            public SignalUnawareInfrastructureReceiver(MessageBrokerContext message)
                => _messages = new Queue<MessageBrokerContext>(new[] { message });

            public Task Acked => _ackedTcs.Task;

            public int AckCount => Volatile.Read(ref _ackCount);

            public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

            public async Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_messages)
                {
                    if (_messages.Count > 0)
                    {
                        return _messages.Dequeue();
                    }
                }

                // Park until cancellation rather than returning null, which would spin the receive loop.
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return null; // unreachable: the await above always throws on cancellation.
            }

            public Task<SettlementResult> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _ackCount);
                _ackedTcs.TrySetResult(true);
                return Task.FromResult(SettlementResult.Settled());
            }

            public Task<SettlementResult> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
                => Task.FromResult(SettlementResult.Settled());

            public Task<SettlementResult> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
                => Task.FromResult(SettlementResult.Settled());

            public Task<int> MessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken)
                => Task.FromResult(1);

            public TransactionScope CreateLocalTransaction(TransactionContext context) => null;

            public Task StopReceiver() => Task.CompletedTask;

            public ValueTask DisposeAsync() => default;

            public void Dispose()
            {
            }
        }
    }
}
