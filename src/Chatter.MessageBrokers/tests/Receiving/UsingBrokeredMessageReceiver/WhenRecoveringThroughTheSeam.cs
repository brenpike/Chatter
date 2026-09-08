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
        // ordered sequence of transitions the store actually MADE — each report answers whether it was
        // the call that transitioned the circuit, so a report the store discards records nothing — and a
        // test can then assert the circuit genuinely opened and half-opened rather than only that it
        // ended Closed (a state it also starts in). Transitions are appended under a lock so the
        // receiver loop's background writes never race the test thread's reads.
        private sealed class RecordingCircuitBreakerStateStore : ICircuitBreakerStateStore
        {
            private readonly InMemoryCircuitBreakerStateStore _inner;
            private readonly object _transitionsLock = new object();
            private readonly List<CircuitBreakerState> _transitions = new List<CircuitBreakerState>();

            // INVARIANT: the receiver loop's own poll for the next message and a worker's dispatch retry
            // both call AdmitAsync on this SAME shared breaker concurrently. AdmitAsync's own admission
            // does not report whether THIS call was the one that flipped Open → HalfOpen (unlike the
            // pre-#432 TryHalfOpenAsync, which returned that as an atomic bool), so HalfOpen is inferred
            // here from a before/after read of _inner.State instead. Left unsynchronized, that read pair
            // is a TOCTOU: a caller that loses the race to perform the transition can still observe
            // HalfOpen afterward (the state stays HalfOpen for every caller until it closes) and record a
            // second, spurious HalfOpen for the one transition another caller actually made. Serializing
            // every AdmitAsync call through this gate restores the old atomicity: the read, the inner
            // call, and the post-check all complete for one caller before the next caller's read begins.
            private readonly SemaphoreSlim _admitGate = new SemaphoreSlim(1, 1);

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

            public async Task<CircuitBreakerAdmission> AdmitAsync(TimeSpan openToHalfOpenWaitTime)
            {
                await _admitGate.WaitAsync();
                try
                {
                    var wasOpen = _inner.State == CircuitBreakerState.Open;
                    var admission = await _inner.AdmitAsync(openToHalfOpenWaitTime);
                    if (wasOpen && _inner.State == CircuitBreakerState.HalfOpen)
                    {
                        Record(CircuitBreakerState.HalfOpen);
                    }

                    return admission;
                }
                finally
                {
                    _admitGate.Release();
                }
            }

            // The inner store returns whether THIS report transitioned the circuit, so a transition is recorded
            // only when one genuinely happened rather than whenever a caller asked for one.
            public async Task<bool> RecordSuccessAsync(CircuitBreakerAdmission admission, int successesToClose)
            {
                var closed = await _inner.RecordSuccessAsync(admission, successesToClose);
                if (closed)
                {
                    Record(CircuitBreakerState.Closed);
                }

                return closed;
            }

            public async Task<bool> RecordFailureAsync(CircuitBreakerAdmission admission, Exception ex, int failuresToOpen)
            {
                var opened = await _inner.RecordFailureAsync(admission, ex, failuresToOpen);
                if (opened)
                {
                    Record(CircuitBreakerState.Open);
                }

                return opened;
            }
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
        // Test (b): a handler failure opens the circuit breaker, and the very next attempt is
        //           admitted straight into a half-open trial that recovers it.
        //
        // COVERAGE — what this dispatch call's retry loop actually does, in order:
        //   1. Attempt 1: the handler runs and throws. ShouldTrip matches InvalidOperationException
        //      and the failure counter reaches NumberOfFailuresBeforeOpen=1, so the circuit opens
        //      (Closed → Open). ShouldRetry matches too, so this failure spends one of the three
        //      budgeted retry attempts.
        //   2. Attempt 2: AdmitAsync finds the store Open with its cooling period
        //      (OpenToHalfOpenWaitTimeInSeconds=0) already elapsed, so the SAME call both moves the
        //      store Open → HalfOpen and admits the trial (CircuitBreakerVerdict.Trial) — one
        //      decision, not a refusal followed by a later trial. The handler runs under the
        //      half-open trial and succeeds, and one success reaches
        //      NumberOfHalfOpenSuccessesToClose=1, so the circuit closes (HalfOpen → Closed).
        //   Because the store now issues the admission itself instead of a caller inferring one from
        //   a stale read, this path never receives CircuitBreakerVerdict.Refused and
        //   CircuitBreakerOpenException is never thrown here — see the NOTE below. Refusal without
        //   execution is pinned by
        //   Recovery/CircuitBreaker/UsingCircuitBreaker/WhenExecuting.cs::MustRefuseWithoutExecutingWhenOpen;
        //   the store's own cooling adjudication by
        //   Recovery/CircuitBreaker/UsingInMemoryCircuitBreakerStateStore/WhenManagingState.cs::MustRefuseAdmissionWhileTheOpenCircuitIsStillCooling
        //   and MustNotAdmitAnythingWhileTheOpenCircuitIsStillCooling; and a refusal still spending
        //   retry budget (when one does occur) by
        //   Recovery/Retry/UsingRetryStrategy/WhenExecuting.cs::MustDelayAndCountAttemptWhenCircuitBreakerRefusesThenSucceeds.
        //   The transitions are observed through RecordingCircuitBreakerStateStore — ObservedTransitions
        //   for the Open and HalfOpen transitions the store actually made, IsClosed for the
        //   final state — and via the final Ack in the receiver's CallLog.
        //
        // ATTEMPT ARITHMETIC — ONE ATTEMPT OF HEADROOM: that sequence spends 2 of the 3 attempts
        //   budgeted by MaxRetryAttempts below — attempt 1 fails and opens the circuit, attempt 2 is
        //   admitted straight into a half-open trial that succeeds and closes it. One attempt is
        //   never used. If a future change spends both remaining attempts anywhere in this loop — an
        //   added refusal, an extra cooling pass, another recovery-wrapped call that also fails — the
        //   loop throws MaxRetryAttemptsExceededException instead of reaching the Ack, and this test
        //   goes red. Read that as a design signal about attempt arithmetic, not as a flaky test:
        //   raising MaxRetryAttempts here would hide the very change worth arguing about.
        //
        // DEFERRED: The precise moment the CB enters Open relative to the receiver's OTHER
        //   recovery-wrapped calls (ReceiveMessageAsync, MessageDeliveryCountAsync, settlement) is
        //   not pinned here, because each of those is its own retry loop sharing the same breaker.
        //   What IS pinned: given exactly one qualifying failure and NumberOfFailuresBeforeOpen=1,
        //   the CB MUST have been Open and MUST have been HalfOpen at some point before the final
        //   Ack, and MUST be Closed after it.
        //
        // NOTE: ShouldTrip matches InvalidOperationException (thrown by the handler) and ShouldRetry
        //   matches it too — ShouldRetry is what buys attempt 2 after the handler's own failure.
        //   Because openToHalfOpenWaitTime=0 s — the value every CircuitBreakerOptionsCreator default
        //   uses — an open circuit's cooling period is always already elapsed by the time anything
        //   asks again, so the store never returns CircuitBreakerVerdict.Refused here: this test does
        //   NOT exercise the refusal path, and no CircuitBreakerOpenException is ever raised in it.
        //   That coverage lives in the breaker's own unit tests referenced above. The cooling wait
        //   this test's path never reaches would be a Task.Delay(0 s) in any case, so no real
        //   wall-clock delay is possible here regardless.
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

            // Retry: ShouldRetry=true for InvalidOperationException. MaxRetryAttempts=3 covers the
            // fail → half-open-trial sequence with ONE attempt of headroom — see the attempt
            // arithmetic note on this test before changing it.
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
            // also starts in). Without this, a regression where a reported failure never opens the
            // circuit, or AdmitAsync never advances Open → HalfOpen, would still leave the store Closed
            // and pass the assertion below, so this test would silently stop pinning the
            // Closed → Open → HalfOpen transition.
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
