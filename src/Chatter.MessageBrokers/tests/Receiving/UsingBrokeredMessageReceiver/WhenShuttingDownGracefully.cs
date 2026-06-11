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
    // INVARIANT: exercises the explicit teardown paths the steady-state tests never call. WhenProcessingConcurrently
    // only cancels the loop token; here StopReceiver(), DisposeAsync(), and synchronous Dispose() are invoked directly
    // WHILE workers are gated in-flight, proving each drains the live worker set to quiescence before disposing the
    // shared primitives and records its infrastructure call exactly once. The receiver serializes every teardown on a
    // single SemaphoreSlim teardown gate and latches a monotonic terminal lifecycle state under that gate; gating
    // primitives (SemaphoreSlim gate + interlocked counter + 15s watchdog) make a teardown regression surface as a
    // Timeout/OperationCanceled, not a hang.
    public class WhenShuttingDownGracefully : Testing.Core.Context
    {
        // INVARIANT: MessageBrokerOptions.TransactionMode has internal set; accessible via InternalsVisibleTo("Chatter.MessageBrokers.Tests").
        private static MessageBrokerOptions BuildOptions(TransactionMode mode = TransactionMode.None)
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = mode;
            return opts;
        }

        // INVARIANT: pass-through IRecoveryStrategy invokes the delegate exactly once, no retry machinery.
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
            Mock<IReceivedMessageDispatcher> dispatcher)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);

            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: PassThroughRecovery().Object,
                receivedMessageDispatcher: dispatcher.Object);
        }

        // INVARIANT: spins until the predicate holds or the watchdog fires, converting a stuck teardown into a prompt
        // OperationCanceledException instead of a hang.
        private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken watchdog)
        {
            while (!predicate())
            {
                watchdog.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        // INVARIANT: builds a dispatcher whose every worker increments the in-flight counter, blocks on the supplied
        // release gate, and decrements on exit. The teardown call is issued only after in-flight has reached the cap,
        // so the drain genuinely has live workers to await.
        private static Mock<IReceivedMessageDispatcher> GatedDispatcher(SemaphoreSlim releaseGate, StrongBox<int> inFlight)
        {
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    Interlocked.Increment(ref inFlight.Value);
                    try
                    {
                        await releaseGate.WaitAsync(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight.Value);
                    }
                });
            return dispatcher;
        }

        private class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }

        // ------------------------------------------------------------------ (a) StopReceiver drains in-flight workers

        [Fact]
        public async Task MustDrainInFlightWorkersAndRecordStopWhenStopReceiverCalled()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Confirm exactly the cap is occupied by blocked workers BEFORE releasing the gate, so StopReceiver's drain
            // has real in-flight work to await.
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            // Release the gate ONLY after confirming in-flight == N, then stop: the workers complete and StopReceiver
            // drains them to quiescence.
            releaseGate.Release(messageCount);

            var stop = sut.StopReceiver();
            var completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(stop, "StopReceiver must drain in-flight workers and complete cleanly");
            await stop; // surface any fault

            Volatile.Read(ref inFlight.Value).Should().Be(0, "all in-flight workers must have quiesced before StopReceiver returns");
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Stop, "StopReceiver must stop the infrastructure receiver");
        }

        // ------------------------------------------------------------------ (b) DisposeAsync cancels, drains, records Dispose once

        [Fact]
        public async Task MustCancelDrainAndRecordDisposeOnceWhenDisposeAsyncCalled()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            // DisposeAsync cancels the loop token (unblocking the gated workers via OCE on the worker token) and drains.
            releaseGate.Release(messageCount);
            var dispose = sut.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(dispose, "DisposeAsync must drain in-flight workers and complete cleanly");
            await dispose;

            Volatile.Read(ref inFlight.Value).Should().Be(0, "all in-flight workers must have quiesced before DisposeAsync returns");
            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "DisposeAsync must dispose the infrastructure receiver exactly once");
        }

        // ------------------------------------------------------------------ (c) synchronous Dispose quiesces off any SynchronizationContext

        [Fact]
        public async Task MustQuiesceWithoutDeadlockWhenSynchronousDisposeCalled()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            // Release the gate so the synchronously-blocking QuiesceForSyncDispose can complete its drain, then call
            // Dispose() OFF any SynchronizationContext (Task.Run) to honor the no-deadlock contract. The sync wait
            // (GetAwaiter().GetResult) must return rather than deadlock because the loop and workers never capture a
            // caller context.
            releaseGate.Release(messageCount);
            var dispose = Task.Run(() => sut.Dispose());
            var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(dispose, "synchronous Dispose must quiesce without deadlocking");
            await dispose;

            Volatile.Read(ref inFlight.Value).Should().Be(0, "all in-flight workers must have quiesced after synchronous Dispose returns");
            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "synchronous Dispose must dispose the infrastructure receiver exactly once");
        }

        // ------------------------------------------------------------------ (d) double Dispose()/DisposeAsync() is idempotent

        [Fact]
        public async Task MustBeIdempotentAcrossDisposeAsyncAndDispose()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            releaseGate.Release(messageCount);

            // First teardown via DisposeAsync, then redundant synchronous Dispose calls. The terminal lifecycle latch
            // (plus the _disposedValue no-op latch on the synchronous path) makes each subsequent synchronous Dispose a
            // no-op, and RecordCallOnce keeps the infrastructure Dispose count at 1.
            var first = sut.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(first, "the first DisposeAsync must complete cleanly");
            await first;

            // The synchronous Dispose path is guarded by the _disposedValue terminal latch, so repeated calls are no-ops
            // that neither throw nor re-dispose the infrastructure receiver.
            sut.Dispose();
            sut.Dispose();

            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "repeated teardown must remain idempotent and record Dispose exactly once");
        }

        // ------------------------------------------------------------------ (e) repeated StopReceiver is single-flight idempotent

        [Fact]
        public async Task MustRecordStopOnceWhenStopReceiverCalledTwice()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Drive the loop to a known in-flight state, then release the gate so the first StopReceiver's drain can
            // complete.
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);
            releaseGate.Release(messageCount);

            var first = sut.StopReceiver();
            var firstCompleted = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(15)));
            firstCompleted.Should().BeSameAs(first, "the first StopReceiver must drain in-flight workers and complete cleanly");
            await first;

            // The second StopReceiver observes the already-completed teardown via the serialized teardown gate's terminal
            // lifecycle latch: it must NOT throw ObjectDisposedException over the disposed token source/semaphore, and
            // must complete promptly.
            var second = sut.StopReceiver();
            var secondCompleted = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(15)));
            secondCompleted.Should().BeSameAs(second, "the second StopReceiver must observe the completed teardown and return without throwing");
            await second; // surface any ObjectDisposedException

            // Without the serialized teardown gate's terminal lifecycle latch the fake's RecordCall would log Stop on
            // BOTH entrypoints; exactly one proves only the first caller ran the teardown body and the second
            // short-circuited on the under-gate terminal-state check.
            infraReceiver.CallLog.Count(c => c == ReceiverCall.Stop)
                .Should().Be(1, "repeated StopReceiver must be single-flight and stop the infrastructure receiver exactly once");
        }

        // ------------------------------------------------------------------ (f) strongest-wins: a Dispose racing a Stop disposes the infra

        [Fact]
        public async Task MustDisposeInfrastructureWhenStopReceiverAndDisposeAsyncRaceConcurrently()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            // Release the gate, then race StopReceiver and DisposeAsync at the same teardown. Strongest-wins teardown
            // strength (Stop < Dispose): whichever entrypoint acquires the serialized teardown gate first, a
            // Dispose-strength entrypoint participated, so the FINAL infrastructure disposition MUST be Disposed — never
            // left merely Stopped. The single-quiesce spirit is preserved (the cancel/await-loop/drain body runs once
            // under the gate, latching the terminal lifecycle), but if a Stop happened to run the body the following
            // Dispose escalates so the infra is actually disposed. Neither caller may surface an ObjectDisposedException
            // over the Exchange-and-disposed token source/semaphore.
            releaseGate.Release(messageCount);
            var race = Task.WhenAll(sut.StopReceiver(), sut.DisposeAsync().AsTask());
            var completed = await Task.WhenAny(race, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(race, "concurrent StopReceiver and DisposeAsync must both quiesce and complete cleanly");
            await race; // surface any ObjectDisposedException from either entrypoint

            Volatile.Read(ref inFlight.Value).Should().Be(0, "all in-flight workers must have quiesced before the racing teardowns return");

            // STRENGTH-MONOTONICITY WITNESS: because a Dispose-strength entrypoint (DisposeAsync) participated in the
            // race, the infrastructure receiver must end DISPOSED regardless of which caller won admission. This is the
            // corrected contract — the prior assertion accepted a terminal Stop-only disposition (count of Stop-or-Dispose
            // == 1), which codified the disposal-strength-loss defect where a Dispose losing the race to a Stop winner
            // left the infra un-disposed. The fake records Dispose at most once (RecordCallOnce), so this also proves the
            // escalation disposes exactly once.
            var snapshot = infraReceiver.CallLog;
            snapshot.Should().Contain(ReceiverCall.Dispose,
                "a Dispose-strength entrypoint participated, so the infrastructure receiver must end disposed, never merely stopped");
            snapshot.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "strength escalation must dispose the infrastructure receiver exactly once");
        }

        // ------------------------------------------------------------------ (g) teardown in the Starting window defers disposal to startup's surrender path

        [Fact]
        public async Task MustTearDownPartialInfrastructureWhenDisposeRacesStartupBeforeGoLive()
        {
            const int maxConcurrentCalls = 1;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 0);

            // Arm the gate so InitializeAsync blocks: under the ownership/handoff model the infra receiver is resolved and
            // InitializeAsync'd into a STARTUP-OWNED LOCAL, never published to the shared _infrastructureReceiver field
            // during this window. The SUT has advanced to the Starting lifecycle state but has NOT reached go-live
            // (IsReceiving stays false) until we release.
            var initializeEntered = infraReceiver.ArmInitializeGate();

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var startup = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Wait until Initialize has been recorded: the receiver is now pinned in the Starting window (startup-owned
            // local being InitializeAsync'd, _infrastructureReceiver field still null, not yet live).
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Initialize), watchdog.Token);
            sut.IsReceiving.Should().BeFalse("the receiver is parked in the Starting window before go-live");

            // Tear down WHILE in the Starting window. The teardown must NOT no-op (only NotStarted is a no-op): it sees a
            // null _infrastructureReceiver field, quiesces nothing observable, latches TornDown, and records its strength.
            // Release the gate so InitializeAsync completes and startup observes TornDown at the handoff and SURRENDERS its
            // startup-owned local — disposing the real receiver on startup's surrender path (deferred disposal).
            var dispose = sut.DisposeAsync().AsTask();
            infraReceiver.ReleaseInitializeGate();

            // Await BOTH the teardown caller AND the startup task: the caller's DisposeAsync latched TornDown over the null
            // field and returned without itself disposing; the disposal is deferred to startup's surrender path. Awaiting
            // startup too pins that disposal to a deterministic completion point instead of racing the assertion.
            var teardowns = Task.WhenAll(dispose, startup);
            var completed = await Task.WhenAny(teardowns, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(teardowns, "a teardown landing in the Starting window must complete cleanly once startup surrenders the startup-owned local");
            await teardowns;

            sut.IsReceiving.Should().BeFalse("a receiver torn down during startup must never report live");

            // The Starting-window teardown deferred disposal to startup's surrender path. Spin to the bounded watchdog so a
            // leak surfaces as a prompt OperationCanceledException rather than a flaky read; the partial infra must end
            // disposed exactly once (RecordCallOnce records Dispose at most once, so this also proves no double-dispose),
            // never no-op'd like a NotStarted teardown.
            await WaitUntilAsync(() => infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose) == 1, watchdog.Token);

            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "a Dispose landing in the Starting window must dispose the partial infrastructure via startup's surrender path exactly once, not no-op like a NotStarted teardown");
        }

        // ------------------------------------------------------------------ (h) NotStarted premature Dispose is a clean no-op that does not latch out a later real teardown

        [Fact]
        public async Task MustNotLatchOutLaterTeardownWhenNeverStartedReceiverDisposedPrematurely()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            // Premature synchronous Dispose BEFORE StartReceiver is ever called (the DI-singleton premature-Dispose, per
            // b91b751). The receiver is NotStarted, so this is a structural no-op: it must not record any infrastructure
            // call (there is no infra receiver resolved yet) and, crucially, must NOT touch the teardown gate or latch
            // the terminal lifecycle state in a way that locks out the host's later genuine teardown.
            sut.Dispose();
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Stop, "a NotStarted Dispose must not stop a non-existent infra receiver");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Dispose, "a NotStarted Dispose must not dispose a non-existent infra receiver");

            // Now the host genuinely starts the receiver, drives it to in-flight, and tears it down via DisposeAsync. The
            // premature Dispose must not have latched the terminal lifecycle, so this real teardown still disposes the infra.
            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            releaseGate.Release(messageCount);
            var dispose = sut.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(dispose, "the genuine post-startup teardown must complete cleanly despite the earlier premature Dispose");
            await dispose;

            Volatile.Read(ref inFlight.Value).Should().Be(0, "all in-flight workers must have quiesced before the real teardown returns");
            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "the premature NotStarted Dispose must not latch the terminal lifecycle out of the genuine teardown, which disposes the infra exactly once");
        }

        // ------------------------------------------------------------------ (i) RENDEZVOUS-ORPHAN: teardowns landing mid-startup defer disposal to startup's surrender path with no double-fire
        //
        // RACE-CLASS WITNESS #2 (startup/teardown rendezvous-orphan). Under the former lock-free design a teardown could
        // win admission while StartReceiverImpl was constructing the loop primitives, observe some of them as null, and
        // leave an orphaned semaphore/token source/loop behind (or double-fire the infra Stop). The SemaphoreSlim gate plus
        // the ownership/handoff model close the class by CONSTRUCTION: StartReceiverImpl resolves and InitializeAsync's the
        // infra receiver into a STARTUP-OWNED LOCAL with the gate NOT held, and never publishes it to _infrastructureReceiver
        // before the gated publish-or-surrender handoff. So a teardown racing the Starting window sees only a null
        // _infrastructureReceiver field: it quiesces nothing observable, latches TornDown, and records its strength. At the
        // handoff startup observes that TornDown and SURRENDERS its local at the recorded (strongest) strength — never a
        // half-built set. Here ArmInitializeGate pins the receiver in the Starting window (startup-owned local being
        // InitializeAsync'd, field still null); concurrent Dispose+Stop teardowns land there, then the gate is released so
        // startup surrenders. The infra must be disposed exactly once (the strongest Dispose strength always wins on the
        // surrender path) and IsReceiving must never go true. The infra Dispose is recorded at most once by the fake
        // (RecordCallOnce), so an exactly-once count also proves there is no double-dispose.
        [Fact]
        public async Task MustTearDownFullPartialSetWithNoDoubleFireWhenTeardownRacesStartupInStartingWindow()
        {
            const int maxConcurrentCalls = 1;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 0);

            // Pin the receiver in the Starting window: InitializeAsync blocks until released, so the SUT is InitializeAsync'ing
            // its startup-owned local and has advanced to Starting but has NOT published the infra receiver, constructed the
            // loop primitives, or gone live.
            _ = infraReceiver.ArmInitializeGate();

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var startup = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Initialize), watchdog.Token);
            sut.IsReceiving.Should().BeFalse("the receiver is parked in the Starting window before go-live");

            // Race a Dispose and a Stop into the Starting window concurrently, THEN release the gate so startup runs the
            // publish-or-surrender handoff. Both teardowns see a null field, quiesce nothing, latch TornDown and record
            // their strengths; startup observes TornDown at the handoff and surrenders the startup-owned local at the
            // strongest (Dispose) strength. No teardown may surface an ObjectDisposedException.
            var dispose = sut.DisposeAsync().AsTask();
            var stop = sut.StopReceiver();
            infraReceiver.ReleaseInitializeGate();

            // Await the racing teardowns AND the startup task: the teardown callers latched TornDown over the null field
            // and returned without themselves disposing; the disposal is deferred to startup's surrender path. Awaiting
            // startup too pins that disposal to a deterministic completion point instead of racing the assertion.
            var race = Task.WhenAll(dispose, stop, startup);
            var completed = await Task.WhenAny(race, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(race, "teardowns racing the Starting window must serialize on the gate and complete cleanly once startup surrenders the local");
            await race;

            sut.IsReceiving.Should().BeFalse("a receiver torn down during startup must never report live");

            // The Starting-window teardowns deferred disposal to startup's surrender path. Spin to the bounded watchdog so a
            // leak surfaces as a prompt OperationCanceledException rather than a flaky read; the partial infra must end
            // disposed exactly once (RecordCallOnce records Dispose at most once, so this also proves no double-dispose).
            await WaitUntilAsync(() => infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose) == 1, watchdog.Token);

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Dispose,
                "a Dispose-strength teardown participated, so the partial infrastructure must end disposed via startup's surrender path, never merely stopped");
            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "the Starting-window teardown must dispose the partial infrastructure via startup's surrender path exactly once (no double-dispose)");
        }

        // ------------------------------------------------------------------ (iter3) INITIALIZING INFRA: a Dispose racing DURING InitializeAsync defers disposal to startup's surrender path
        //
        // RACE-CLASS WITNESS (initializing-infrastructure handoff). The ownership/handoff model resolves and InitializeAsync's
        // the infra receiver into a STARTUP-OWNED LOCAL with the teardown gate NOT held, and never publishes it to the shared
        // _infrastructureReceiver field until the gated publish-or-surrender handoff AFTER InitializeAsync returns. This witness
        // pins the receiver mid-InitializeAsync (init blocked on the gate) and issues a Dispose-strength teardown WHILE init is
        // still in flight. Because the blocking InitializeAsync I/O runs with the gate NOT held, the Dispose acquires the gate,
        // sees a null _infrastructureReceiver field, quiesces nothing observable, latches TornDown + records Dispose strength,
        // and RETURNS without itself disposing the still-initializing local. When the gate is released InitializeAsync completes
        // and startup's handoff observes TornDown and SURRENDERS the startup-owned local at Dispose strength — disposing the real
        // receiver exactly once on startup's surrender path (deferred disposal). This FAILS on the pre-STEP-001 gate-after-await
        // structure (where init ran UNDER the gate, so a concurrent teardown could not acquire it during init, or the field was
        // published before init so the teardown disposed an initializing receiver) and PASSES on the handoff model.
        [Fact]
        public async Task MustTearDownInitializingInfrastructureWhenDisposeRacesDuringInitializeAsync()
        {
            const int maxConcurrentCalls = 1;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 0);

            // Arm the gate so InitializeAsync blocks: the SUT pins mid-InitializeAsync on its startup-owned local, with the
            // teardown gate NOT held (the blocking init I/O runs outside the gate under the handoff model).
            var initializeEntered = infraReceiver.ArmInitializeGate();

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var startup = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Wait until Initialize has been recorded: the receiver is now pinned mid-InitializeAsync (startup-owned local
            // being initialized, _infrastructureReceiver field still null, not yet live).
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Initialize), watchdog.Token);
            sut.IsReceiving.Should().BeFalse("the receiver is parked mid-InitializeAsync before go-live");

            // Issue a Dispose-strength teardown WHILE init is still blocked. Because init holds NO teardown gate, the Dispose
            // acquires the gate, sees a null field, latches TornDown + records Dispose strength, and returns without disposing
            // the still-initializing local. THEN release the gate so InitializeAsync completes and startup surrenders.
            var dispose = sut.DisposeAsync().AsTask();
            infraReceiver.ReleaseInitializeGate();

            // Await BOTH the teardown task AND the startup task: the caller's DisposeAsync latched TornDown over the null
            // field and deferred disposal to startup's surrender path. Awaiting startup pins that disposal to a deterministic
            // completion point instead of racing the assertion.
            var teardowns = Task.WhenAll(dispose, startup);
            var completed = await Task.WhenAny(teardowns, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(teardowns, "a Dispose racing during InitializeAsync must complete cleanly once startup surrenders the startup-owned local");
            await teardowns;

            sut.IsReceiving.Should().BeFalse("a receiver torn down while initializing must never report live");

            // The mid-init Dispose deferred disposal to startup's surrender path. Spin to the bounded watchdog so a leak
            // surfaces as a prompt OperationCanceledException; the initializing infra must end disposed exactly once
            // (RecordCallOnce records Dispose at most once, so this also proves no double-dispose), with no
            // ObjectDisposedException surfaced by either the teardown caller or the startup surrender path (both were awaited
            // clean above).
            await WaitUntilAsync(() => infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose) == 1, watchdog.Token);

            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "a Dispose racing during InitializeAsync must dispose the initializing infrastructure via startup's surrender path exactly once");
        }

        // ------------------------------------------------------------------ (i2) NULL-RECEIVER CLAIM WINDOW: a Dispose landing before the infra receiver is assigned must not latch the claim over null and leak the real receiver
        //
        // RACE-CLASS WITNESS #4 (null-receiver disposal-claim leak). The InitializeAsync gate (facts g/i) can only pin the
        // SUT AFTER _infrastructureReceiver is assigned; it cannot reach the narrower window between the NotStarted->Starting
        // CAS and the synchronous GetReceiver assignment, where _infrastructureReceiver is still null. The GetReceiver gate
        // pins exactly there. A Dispose-strength teardown landing in that window must NOT claim _infrastructureDisposed 0->1
        // over a null receiver: pre-fix the disposal primitive latched the claim BEFORE the null check, so when GetReceiver
        // then returned the real receiver and startup InitializeAsync'd it, the go-live TornDown teardown found the claim
        // already latched and skipped disposing it — leaking the real receiver un-disposed (Dispose count 0). Post-fix the
        // primitive captures the receiver to a local and returns with NO claim when null, so the later teardown disposes the
        // real receiver exactly once.
        [Fact]
        public async Task MustNotLatchClaimOverNullReceiverWhenDisposeLandsBeforeInfraReceiverAssigned()
        {
            const int maxConcurrentCalls = 1;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 0);

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);

            // Arm the GetReceiver gate BEFORE constructing/starting the SUT so the synchronous GetReceiver blocks: the SUT
            // will have advanced NotStarted->Starting but NOT yet assigned _infrastructureReceiver (this call's return), so a
            // teardown lands in the null-receiver window the InitializeAsync gate cannot reach.
            var getReceiverEntered = provider.ArmGetReceiverGate();

            var sut = new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: PassThroughRecovery().Object,
                receivedMessageDispatcher: dispatcher.Object);

            using var cts = new CancellationTokenSource();
            var startup = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Wait until GetReceiver has parked: the receiver is now pinned in the null-receiver window (Starting reached,
            // _infrastructureReceiver still null).
            var entered = await Task.WhenAny(getReceiverEntered, Task.Delay(TimeSpan.FromSeconds(15)));
            entered.Should().BeSameAs(getReceiverEntered, "the synchronous GetReceiver must reach the gate before the infra receiver is assigned");
            await getReceiverEntered;
            sut.IsReceiving.Should().BeFalse("the receiver is parked in the null-receiver window before the infra receiver is assigned");

            // Issue a Dispose-strength teardown WHILE _infrastructureReceiver is still null, THEN release the gate so
            // GetReceiver returns the real receiver and startup InitializeAsync's it and reaches the go-live TornDown branch.
            var dispose = sut.DisposeAsync().AsTask();
            provider.ReleaseGetReceiverGate();

            // Await BOTH the teardown caller AND the startup task: when the Dispose lands in the null-receiver window it
            // latches the receiver TornDown and returns, deferring the real receiver's disposal to startup's go-live
            // TornDown branch (the caller's DisposeAsync did not itself dispose the not-yet-assigned receiver). Awaiting
            // startup too pins that disposal to a deterministic completion point instead of racing the assertion.
            var teardowns = Task.WhenAll(dispose, startup);
            var completed = await Task.WhenAny(teardowns, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(teardowns, "a teardown landing in the null-receiver window must complete cleanly once startup resolves the real receiver");
            await teardowns;

            sut.IsReceiving.Should().BeFalse("a receiver torn down during startup must never report live");

            // The Dispose landing over a null receiver must NOT have latched the claim: once startup InitializeAsync's the
            // real receiver, the go-live TornDown branch must dispose it exactly once. Spin to the bounded watchdog so a leak
            // surfaces as a prompt OperationCanceledException rather than a flaky read. Pre-fix the null-window Dispose latched
            // the claim over null, so the go-live TornDown branch found the claim already taken and skipped disposing the real
            // receiver — Dispose count stays 0 and this spin times out.
            await WaitUntilAsync(() => infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose) == 1, watchdog.Token);

            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "a Dispose-strength teardown landing before the receiver was assigned must not latch the claim over null and leak the real receiver built afterward");
        }

        // ------------------------------------------------------------------ (j) SYNC-DISPOSE FAULT-RETRY: a faulting synchronous Dispose does not latch, so a second Dispose re-runs
        //
        // RACE-CLASS WITNESS #3 (sync-dispose latch composes with fault-reset). Under the former design the synchronous
        // Dispose set _disposedValue unconditionally (even when the quiesce threw), permanently latching the receiver out
        // of teardown and LEAKING the infra un-disposed. The rewrite drives the synchronous Dispose through the SAME
        // serialized quiesce and latches _disposedValue ONLY after a successful quiesce — a thrown quiesce leaves the
        // latch UNSET (retryable), mirroring the async fault-resettable contract. Here ArmThrowOnceOnTeardown faults the
        // FIRST synchronous Dispose (run off-context via Task.Run to honor the no-deadlock contract); a SECOND Dispose
        // must re-run and dispose the infra exactly once, proving the sync latch did not strand the receiver.
        [Fact]
        public async Task MustRetrySynchronousDisposeWhenFirstQuiesceFaultsThenSecondDisposes()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // The FIRST infra teardown call throws once at the infra-teardown seam, faulting the first synchronous
            // Dispose's quiesce body so its _disposedValue latch must NOT set.
            infraReceiver.ArmThrowOnceOnTeardown();

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            // Release the gate so the drain can quiesce the workers, then call the synchronous Dispose OFF any
            // SynchronizationContext (Task.Run) so its GetAwaiter().GetResult() stays deadlock-free. The injected fault
            // surfaces inside the quiesce; Dispose() swallows it (Dispose must not throw) and returns WITHOUT latching.
            releaseGate.Release(messageCount);

            var firstDispose = Task.Run(() => sut.Dispose());
            var firstCompleted = await Task.WhenAny(firstDispose, Task.Delay(TimeSpan.FromSeconds(15)));
            firstCompleted.Should().BeSameAs(firstDispose, "the faulting synchronous Dispose must swallow the fault and return without deadlocking");
            await firstDispose;

            Volatile.Read(ref inFlight.Value).Should().Be(0, "the first Dispose still drained the in-flight workers before its infra-teardown faulted");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Dispose, "the faulting first synchronous Dispose must not have recorded a successful infra Dispose");

            // The throw-once is consumed, so a SECOND synchronous Dispose must re-run the gate body (the latch did not
            // set) and dispose the infra exactly once — behaviorally proving _disposedValue did not latch on the fault.
            var secondDispose = Task.Run(() => sut.Dispose());
            var secondCompleted = await Task.WhenAny(secondDispose, Task.Delay(TimeSpan.FromSeconds(15)));
            secondCompleted.Should().BeSameAs(secondDispose, "the second synchronous Dispose must re-run cleanly after the first faulted");
            await secondDispose;

            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "the retrying synchronous Dispose must dispose the infrastructure receiver exactly once");
        }

        // ------------------------------------------------------------------ (k) GATED-ESCALATION RACE: an in-flight escalation DisposeAsync that faults must not let a loser leak the infra
        //
        // RESIDUAL-RACE WITNESS. Before the escalation was folded inside _teardownGate, EscalateInfrastructureDisposalIfRequiredAsync
        // ran OUTSIDE the gate and reused _infrastructureDisposed as both an in-progress dispose claim AND a completed-dispose
        // latch. In a Stop-then-concurrent-Dispose race where the in-flight escalation DisposeAsync then FAULTS, a concurrent
        // Dispose loser observed the in-progress claim (_infrastructureDisposed == 1) as a COMPLETED success, fell through to
        // Dispose(false) — nulling _infrastructureReceiver and latching _disposedValue — and the faulting DisposeAsync then
        // reset the claim, leaking the infra UNRETRYABLY (the _disposedValue fast path short-circuits every later teardown).
        //
        // The fix folds the escalation INSIDE the single gate acquisition, making claim->dispose->reset atomic under the gate:
        // a loser blocks on the gate until the in-flight DisposeAsync completes (or faults and resets), so it can never observe
        // an in-flight claim as completed. Here a Stop runs the quiesce body at Stop-strength (infra Stopped, not disposed),
        // then TWO concurrent Disposes both reach the escalation. The first parks on the gate-then-throw-once hook (deterministic
        // via DisposeAsyncGateEntered — no wall-clock sleep); the second is started only after the first has parked. On release
        // the first DisposeAsync throws once; a subsequent teardown must RETRY and dispose the infra exactly once, and no
        // entrypoint may surface an ObjectDisposedException.
        [Fact]
        public async Task MustNotLeakInfrastructureWhenInFlightEscalationDisposeAsyncFaultsWhileLoserReachesShortCircuit()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            // Drive the loop to a known in-flight state, then drain the workers and run a Stop FIRST so the quiesce body runs
            // at Stop-strength only: infra is Stopped (not disposed), _infrastructureDisposed stays 0, lifecycle latches
            // TornDown. This guarantees the subsequent Disposes dispose via the ESCALATION path (not in the body).
            releaseGate.Release(messageCount);
            var stop = sut.StopReceiver();
            var stopCompleted = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15)));
            stopCompleted.Should().BeSameAs(stop, "the Stop must drain in-flight workers and complete cleanly before the Disposes race the escalation");
            await stop;
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Stop, "the Stop ran the quiesce body at Stop-strength, leaving the infra stopped-not-disposed");

            // Arm the in-flight DisposeAsync to park on the hook gate then throw exactly once. The FIRST escalation DisposeAsync
            // hits this hook; the second teardown's DisposeAsync (after the throw-once is consumed) records normally.
            infraReceiver.ArmGateThenThrowOnceOnDisposeAsync();

            // First Dispose: its escalation runs DisposeAsync, which parks on the hook gate (holding the teardown gate, post-fix).
            var firstDispose = sut.DisposeAsync().AsTask();

            // Deterministically wait until the first DisposeAsync has PARKED on the hook gate before issuing the loser — no sleep.
            var entered = await Task.WhenAny(infraReceiver.DisposeAsyncGateEntered, Task.Delay(TimeSpan.FromSeconds(15)));
            entered.Should().BeSameAs(infraReceiver.DisposeAsyncGateEntered, "the first escalation DisposeAsync must reach the hook gate");
            await infraReceiver.DisposeAsyncGateEntered;

            // Loser: a SECOND concurrent Dispose. Pre-fix it would observe the in-flight _infrastructureDisposed claim as
            // completed and fall through to Dispose(false), nulling/latching the receiver. Post-fix it blocks on the teardown
            // gate the first Dispose still holds.
            var secondDispose = sut.DisposeAsync().AsTask();

            // Release the hook so the first (parked) DisposeAsync throws once.
            infraReceiver.ReleaseDisposeAsyncGate();

            // (a) NO CALLER HANGS: both Disposes settle within the watchdog. The first DisposeAsync FAULTS once with the
            // injected InvalidOperationException (expected); the loser/retry must complete. Neither may surface an
            // ObjectDisposedException over an Exchange-and-disposed primitive.
            var both = Task.WhenAll(SettleAsync(firstDispose), SettleAsync(secondDispose));
            var settled = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(15)));
            settled.Should().BeSameAs(both, "neither teardown caller may hang");
            var outcomes = await both;

            outcomes.Should().NotContain(o => o is ObjectDisposedException,
                "no teardown entrypoint may surface an ObjectDisposedException over a disposed primitive");

            // The injected fault may surface from the first (parked-then-thrown) DisposeAsync; that single InvalidOperationException
            // is expected. Every other caller must settle cleanly (null) — anything else faulting is a regression.
            outcomes.Should().OnlyContain(o => o == null || o is InvalidOperationException,
                "the only tolerated fault is the injected in-flight DisposeAsync throw-once");

            // (b) INFRA NOT LEFT NULLED-BUT-UNDISPOSED: a subsequent teardown must RETRY and actually dispose the infra. Pre-fix
            // the loser had nulled _infrastructureReceiver and latched _disposedValue while the in-flight DisposeAsync threw and
            // reset the claim, so this retry would short-circuit on the _disposedValue fast path and NEVER record Dispose.
            var retrySettle = SettleAsync(sut.DisposeAsync().AsTask());
            var retryCompleted = await Task.WhenAny(retrySettle, Task.Delay(TimeSpan.FromSeconds(15)));
            retryCompleted.Should().BeSameAs(retrySettle, "the retry teardown must settle within the watchdog, not hang");
            (await retrySettle).Should().BeNull("the retry teardown must complete cleanly without surfacing any exception (and in particular no ObjectDisposedException)");

            infraReceiver.CallLog.Count(c => c == ReceiverCall.Dispose)
                .Should().Be(1, "after the in-flight escalation DisposeAsync faulted, a later teardown must retry and dispose the infra exactly once — never leak it unretryably");
        }

        // ------------------------------------------------------------------ (l) CROSS-EPOCH STRENGTH LEAK: a pre-start Dispose-strength must not leak into a later genuine Stop
        //
        // EPOCH-SCOPED STRENGTH WITNESS. The teardown-strength primitive must be scoped to a single started epoch, not a
        // process-wide monotonic max. Under the OLD monotonic-leak primitive a premature SYNCHRONOUS Dispose on a NotStarted
        // receiver recorded a Dispose strength (2) that survived into the next started epoch; when the host then genuinely
        // started the receiver, drove it in-flight, and issued a genuine StopReceiver, that Stop's quiesce body observed the
        // STALE Dispose strength and ESCALATED to a Dispose — leaving the infra DISPOSED instead of merely STOPPED. The fix
        // (STEP-001) raises requested strength only AFTER the NotStarted no-op fast-path (so a premature Dispose records NO
        // strength) and resets requested strength to None when a real teardown completes, scoping strength to the epoch. Here
        // the premature Dispose records no infra call at all (NotStarted no-op), then a genuine Stop on the SAME instance must
        // end the infra STOPPED, never DISPOSED. On the old primitive the leaked Dispose=2 makes the later Stop escalate to a
        // Dispose, so NotContain(Dispose) fails — that is the falsifiability.
        [Fact]
        public async Task MustNotLeakPreStartDisposeStrengthIntoLaterGenuineStop()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 6;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            var inFlight = new StrongBox<int>(0);
            using var releaseGate = new SemaphoreSlim(0, messageCount);
            var dispatcher = GatedDispatcher(releaseGate, inFlight);

            var sut = CreateSut(infraReceiver, dispatcher);

            // Premature synchronous Dispose BEFORE StartReceiver is ever called: the receiver is NotStarted, so this is a
            // structural no-op that records NO infrastructure call AND (post STEP-001) records NO teardown strength — the
            // RaiseRequestedStrength now runs only after the NotStarted no-op fast-path. Assert the CallLog is empty of any
            // infra teardown at this point.
            sut.Dispose();
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Stop, "a NotStarted Dispose must not stop a non-existent infra receiver");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Dispose, "a NotStarted Dispose must not dispose a non-existent infra receiver");

            // Now the host genuinely starts the receiver, drives it to in-flight, and tears it down via a genuine StopReceiver
            // on the SAME instance. The premature Dispose must not have leaked a Dispose strength into this fresh epoch.
            using var cts = new CancellationTokenSource();
            _ = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Volatile.Read(ref inFlight.Value) == maxConcurrentCalls, watchdog.Token);

            releaseGate.Release(messageCount);
            var stop = sut.StopReceiver();
            var completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(stop, "the genuine StopReceiver must drain in-flight workers and complete cleanly despite the earlier premature Dispose");
            await stop;

            Volatile.Read(ref inFlight.Value).Should().Be(0, "all in-flight workers must have quiesced before the genuine StopReceiver returns");

            // CROSS-EPOCH ASSERTION: a pre-start Dispose-strength must not leak into a later genuine Stop; infra must end
            // STOPPED, not DISPOSED. On the old monotonic-leak primitive the stale Dispose=2 makes this Stop escalate to a
            // Dispose, so NotContain(Dispose) fails.
            var snapshot = infraReceiver.CallLog;
            snapshot.Should().Contain(ReceiverCall.Stop, "the genuine StopReceiver must stop the infrastructure receiver");
            snapshot.Should().NotContain(ReceiverCall.Dispose,
                "a pre-start Dispose-strength must not leak into a later genuine Stop; infra must end STOPPED, not DISPOSED");
        }

        // INVARIANT: awaits a teardown task to completion and returns the surfaced exception (or null), so the witness can
        // classify outcomes (tolerate the injected InvalidOperationException, fail on ObjectDisposedException) without an
        // unobserved faulted task tearing the test down.
        private static async Task<Exception> SettleAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }
}
