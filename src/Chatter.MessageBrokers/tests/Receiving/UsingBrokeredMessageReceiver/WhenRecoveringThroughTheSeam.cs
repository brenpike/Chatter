#nullable disable

using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
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
    // INVARIANT: Each test constructs a fresh SUT, receiver, and all real recovery objects.
    // No shared mutable static state is permitted in this class. See #128.
    public class WhenRecoveringThroughTheSeam : Testing.Core.Context
    {
        // ------------------------------------------------------------------ helpers: options

        // INVARIANT: TransactionMode has an internal setter; accessible via InternalsVisibleTo("Chatter.MessageBrokers.Tests").
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

        // INVARIANT: body must deserialise cleanly as FakeMessage for handler tests.
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

        // ------------------------------------------------------------------ helpers: recovery construction

        // INVARIANT: RetryStrategy is an internal type; NullLogger is used instead of a Moq proxy because
        // Castle.DynamicProxy cannot create a proxy for ILogger<T> when T is a non-public (internal) type.
        private static RetryStrategy BuildRetryStrategy(
            RecoveryOptions recoveryOptions,
            IRetryExceptionEvaluator exceptionEvaluator)
            => new RetryStrategy(
                recoveryOptions,
                NullLogger<RetryStrategy>.Instance,
                new NoDelayRetry(),
                exceptionEvaluator);

        // INVARIANT: CircuitBreaker is public sealed; CircuitBreakerExceptionEvaluator is internal and
        // accessible via InternalsVisibleTo. InMemoryCircuitBreakerStateStore is public — held by the
        // test for post-run state observation.
        private static (CircuitBreaker circuitBreaker, InMemoryCircuitBreakerStateStore stateStore)
            BuildCircuitBreaker(
                CircuitBreakerOptions cbOptions,
                ICircuitBreakerExceptionEvaluator exceptionEvaluator)
        {
            var stateStore = new InMemoryCircuitBreakerStateStore(
                NullLogger<InMemoryCircuitBreakerStateStore>.Instance);
            var cb = new CircuitBreaker(
                stateStore,
                cbOptions,
                NullLogger<CircuitBreaker>.Instance,
                exceptionEvaluator);
            return (cb, stateStore);
        }

        // INVARIANT: RetryWithCircuitBreakerStrategy is internal and accessible via InternalsVisibleTo.
        private static RetryWithCircuitBreakerStrategy BuildStrategy(
            RecoveryOptions recoveryOptions,
            CircuitBreaker circuitBreaker,
            IRetryExceptionEvaluator retryEvaluator)
            => new RetryWithCircuitBreakerStrategy(
                recoveryOptions,
                circuitBreaker,
                BuildRetryStrategy(recoveryOptions, retryEvaluator));

        // ------------------------------------------------------------------ helpers: SUT construction

        private static BrokeredMessageReceiver<FakeMessage> BuildSut(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            IRecoveryStrategy strategy,
            Mock<IReceivedMessageDispatcher> dispatcher)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);
            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildBrokerOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: strategy,
                receivedMessageDispatcher: dispatcher.Object);
        }

        // ------------------------------------------------------------------ determinism helper (mirrors WhenReceiving.cs)

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
        // Poll the CallLog until the expected disposition appears, then return.
        // The watchdog CTS (10 s) bounds the wait in case of unexpected code paths.
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

        // ------------------------------------------------------------------ message type

        private class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }

        // ------------------------------------------------------------------ recording state-store decorator
        //
        // INVARIANT: The circuit breaker drives state transitions through ICircuitBreakerStateStore,
        // but the store overwrites State on each transition (Open → HalfOpen → Closed), so the
        // intermediate Open/HalfOpen states are lost by the time the test inspects the final State.
        // This decorator delegates to the real InMemoryCircuitBreakerStateStore and records the
        // ordered sequence of transitions actually requested, so a test can assert the circuit
        // genuinely opened and half-opened rather than only that it ended Closed (a state it also
        // starts in). Transitions are appended under a lock so the receiver loop's background writes
        // never race the test thread's reads.
        private sealed class RecordingCircuitBreakerStateStore : ICircuitBreakerStateStore
        {
            private readonly InMemoryCircuitBreakerStateStore _inner;
            private readonly object _transitionsLock = new object();
            private readonly List<CircuitBreakerState> _transitions = new List<CircuitBreakerState>();

            public RecordingCircuitBreakerStateStore(InMemoryCircuitBreakerStateStore inner)
                => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

            // Locked snapshot so test-thread reads never touch the backing List<T> while the
            // receiver loop mutates it via the transition methods below.
            public IReadOnlyList<CircuitBreakerState> ObservedTransitions
            {
                get { lock (_transitionsLock) { return _transitions.ToArray(); } }
            }

            private void Record(CircuitBreakerState state)
            {
                lock (_transitionsLock) { _transitions.Add(state); }
            }

            public Exception LastException => _inner.LastException;
            public DateTime LastStateChangedDateUtc => _inner.LastStateChangedDateUtc;
            public bool IsClosed => _inner.IsClosed;
            public CircuitBreakerState State => _inner.State;
            public int FailureCount => _inner.FailureCount;
            public int SuccessCount => _inner.SuccessCount;

            public Task OpenAsync(Exception ex)
            {
                Record(CircuitBreakerState.Open);
                return _inner.OpenAsync(ex);
            }

            public Task HalfOpenAsync()
            {
                Record(CircuitBreakerState.HalfOpen);
                return _inner.HalfOpenAsync();
            }

            public Task CloseAsync()
            {
                Record(CircuitBreakerState.Closed);
                return _inner.CloseAsync();
            }

            public Task<int> IncrementSuccessCounterAsync() => _inner.IncrementSuccessCounterAsync();
            public Task<int> IncrementFailureCounterAsync(Exception ex) => _inner.IncrementFailureCounterAsync(ex);
        }

        // ------------------------------------------------------------------
        // Test (a): handler fails on first delivery, succeeds on second.
        //
        // COVERAGE: RetryStrategy re-invokes the handler (retry path observed through the loop).
        // CircuitBreaker: ShouldTrip=false (empty predicates provider list) so one handler
        // failure increments the failure counter but never reaches NumberOfFailuresBeforeOpen=2,
        // leaving the CB closed for the duration. The retry re-invocation is the primary
        // assertion here; CB open/closed transition is NOT the focus of this test.
        //
        // DEFERRED: Asserting the CB failure-counter value directly requires reaching into
        // InMemoryCircuitBreakerStateStore.FailureCount (public property). We deliberately
        // do NOT assert it here because the counter may be incremented by any ExecuteAsync
        // call (ReceiveMessageAsync, DeliveryCountAsync, Ack, etc.) that happens to throw —
        // not only the handler call — making the exact count fragile to assert through the
        // loop seam. Deferred to a dedicated unit test if exact failure-count sequencing
        // must be pinned.
        // ------------------------------------------------------------------
        [Fact]
        public async Task MustRetryHandlerAndAckOnEventualSuccess()
        {
            // Arrange
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext());

            var dispatchCallCount = 0;
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(
                    It.IsAny<FakeMessage>(),
                    It.IsAny<MessageBrokerContext>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    dispatchCallCount++;
                    if (dispatchCallCount == 1)
                        throw new InvalidOperationException("transient failure on first attempt");
                    return Task.CompletedTask;
                });

            // CB: ShouldTrip=false for all exceptions (empty predicates list → ShouldTrip always false).
            // This ensures one handler failure does not open the circuit breaker.
            var cbOptions = new CircuitBreakerOptions
            {
                OpenToHalfOpenWaitTimeInSeconds = 0,
                ConcurrentHalfOpenAttempts = 1,
                NumberOfFailuresBeforeOpen = 2,
                NumberOfHalfOpenSuccessesToClose = 1,
                SecondsOpenBeforeCriticalFailureNotification = 0,
            };
            var cbEvaluator = new CircuitBreakerExceptionEvaluator(
                Array.Empty<ICircuitBreakerExceptionPredicatesProvider>());
            var (circuitBreaker, _) = BuildCircuitBreaker(cbOptions, cbEvaluator);

            // Retry: ShouldRetry=true for InvalidOperationException; MaxRetryAttempts=3 covers two attempts.
            var recoveryOptions = new RecoveryOptions { MaxRetryAttempts = 3 };
            var retryEvaluator = new RetryExceptionEvaluator(
                new[] { new ConfigRetryExceptionPredicatesProvider(
                    new Predicate<Exception>[] { e => e is InvalidOperationException }) });
            var strategy = BuildStrategy(recoveryOptions, circuitBreaker, retryEvaluator);

            var sut = BuildSut(infraReceiver, strategy, dispatcher);

            // Act
            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Ack, watchdog.Token);

            cts.Cancel();
            await loop;

            // Assert: handler was invoked more than once (retry path), and the message was Ack'd.
            dispatchCallCount.Should().BeGreaterThan(1,
                because: "RetryStrategy must re-invoke the handler after the first failure");
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Ack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter);
        }

        // ------------------------------------------------------------------
        // Test (b): enough failures to open the circuit breaker, then handler succeeds
        //           via the half-open recovery path.
        //
        // COVERAGE:
        //   - CB Closed → Open transition: first handler failure increments the counter
        //     to NumberOfFailuresBeforeOpen=1 → CB opens.
        //   - CB Open → HalfOpen: CircuitBreaker.ExecuteAsync waits Task.Delay(0 s) and
        //     transitions to HalfOpen on the next retry attempt.
        //   - CB HalfOpen → Closed: handler succeeds in HalfOpen; one success reaches
        //     NumberOfHalfOpenSuccessesToClose=1 → CB closes.
        //   - The CB state transitions are observed via InMemoryCircuitBreakerStateStore.State
        //     after the loop completes, and via the final Ack in the receiver's CallLog.
        //
        // DEFERRED: The precise moment the CB enters Open (between which two loop iterations)
        //   is not pinned here because it depends on the interleaving of ExecuteAsync calls
        //   (ReceiveMessageAsync vs. DispatchAsync vs. DeliveryCountAsync). What IS pinned:
        //   given exactly one qualifying failure and NumberOfFailuresBeforeOpen=1, the CB
        //   MUST have been Open at some point before the final Ack, and MUST be Closed after it.
        //
        // NOTE: ShouldTrip predicate matches InvalidOperationException (thrown by the handler).
        //   ShouldRetry also matches InvalidOperationException so the retry wrapper re-attempts
        //   after the CB opens. Because openToHalfOpenWaitTime=0 s, the half-open transition
        //   is immediate; no real wall-clock delay occurs.
        // ------------------------------------------------------------------
        [Fact]
        public async Task MustOpenCircuitBreakerOnFailureThenRecoverViaHalfOpen()
        {
            // Arrange
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext());

            var dispatchCallCount = 0;
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(
                    It.IsAny<FakeMessage>(),
                    It.IsAny<MessageBrokerContext>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    dispatchCallCount++;
                    if (dispatchCallCount == 1)
                        throw new InvalidOperationException("failure that trips the circuit");
                    return Task.CompletedTask;
                });

            // CB: ShouldTrip=true for InvalidOperationException; threshold=1 so the first
            // handler failure opens the circuit immediately. openToHalfOpenWaitTime=0 so no
            // real wall-clock delay in the half-open transition.
            var cbOptions = new CircuitBreakerOptions
            {
                OpenToHalfOpenWaitTimeInSeconds = 0,
                ConcurrentHalfOpenAttempts = 1,
                NumberOfFailuresBeforeOpen = 1,
                NumberOfHalfOpenSuccessesToClose = 1,
                SecondsOpenBeforeCriticalFailureNotification = 0,
            };
            var cbEvaluator = new CircuitBreakerExceptionEvaluator(
                new[] { new ConfigCircuitBreakerExceptionPredicatesProvider(
                    new Predicate<Exception>[] { e => e is InvalidOperationException }) });

            // Wrap the real state store in a recording decorator so the test can assert the circuit
            // actually transitioned through Open and HalfOpen, not just that it ended Closed (which it
            // also starts as). The CircuitBreaker is constructed directly here against the recorder.
            var stateStore = new RecordingCircuitBreakerStateStore(
                new InMemoryCircuitBreakerStateStore(
                    NullLogger<InMemoryCircuitBreakerStateStore>.Instance));
            var circuitBreaker = new CircuitBreaker(
                stateStore,
                cbOptions,
                NullLogger<CircuitBreaker>.Instance,
                cbEvaluator);

            // Retry: ShouldRetry=true for InvalidOperationException; MaxRetryAttempts=3 gives
            // the loop enough room to absorb the first failure and re-enter the CB.
            var recoveryOptions = new RecoveryOptions { MaxRetryAttempts = 3 };
            var retryEvaluator = new RetryExceptionEvaluator(
                new[] { new ConfigRetryExceptionPredicatesProvider(
                    new Predicate<Exception>[] { e => e is InvalidOperationException }) });
            var strategy = BuildStrategy(recoveryOptions, circuitBreaker, retryEvaluator);

            var sut = BuildSut(infraReceiver, strategy, dispatcher);

            // Act
            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AwaitDrainedAsync(infraReceiver, watchdog.Token);
            await WaitForDispositionAsync(infraReceiver, ReceiverCall.Ack, watchdog.Token);

            cts.Cancel();
            await loop;

            // Assert: handler was invoked at least twice (fail → CB open → half-open → succeed).
            dispatchCallCount.Should().BeGreaterThan(1,
                because: "handler must be re-attempted after the circuit breaker transitions through half-open");

            // CB must have actually opened and half-opened — not merely ended Closed (the state it
            // also starts in). Without this, a regression where the breaker never calls
            // OpenAsync/HalfOpenAsync would still leave the store Closed and pass the assertion below,
            // so this test would silently stop pinning the Closed → Open → HalfOpen transition.
            var observedTransitions = stateStore.ObservedTransitions;
            observedTransitions.Should().Contain(CircuitBreakerState.Open,
                because: "the qualifying failure must trip the circuit breaker to Open before recovery");
            observedTransitions.Should().Contain(CircuitBreakerState.HalfOpen,
                because: "the circuit breaker must transition through HalfOpen on the recovery attempt");

            // CB must be Closed after successful half-open recovery (NumberOfHalfOpenSuccessesToClose=1).
            stateStore.IsClosed.Should().BeTrue(
                because: "one successful half-open attempt must close the circuit breaker");

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Ack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter);
        }
    }
}
