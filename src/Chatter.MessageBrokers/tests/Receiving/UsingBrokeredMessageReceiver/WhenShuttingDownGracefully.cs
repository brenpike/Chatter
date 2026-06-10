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
    // shared primitives and records its infrastructure call exactly once. Gating primitives (SemaphoreSlim gate +
    // interlocked counter + 15s watchdog) make a teardown regression surface as a Timeout/OperationCanceled, not a hang.
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

            // First teardown via DisposeAsync, then redundant synchronous Dispose calls. The _disposedValue guard makes
            // each subsequent synchronous Dispose a no-op, and RecordCallOnce keeps the infrastructure Dispose count at 1.
            var first = sut.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(first, "the first DisposeAsync must complete cleanly");
            await first;

            // The synchronous Dispose path is guarded by _disposedValue, so repeated calls are no-ops that neither throw
            // nor re-dispose the infrastructure receiver.
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

            // The second StopReceiver observes the already-completed teardown via the single-flight guard: it must NOT
            // throw ObjectDisposedException over the disposed token source/semaphore, and must complete promptly.
            var second = sut.StopReceiver();
            var secondCompleted = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(15)));
            secondCompleted.Should().BeSameAs(second, "the second StopReceiver must observe the completed teardown and return without throwing");
            await second; // surface any ObjectDisposedException

            // Without the single-flight guard the fake's RecordCall would log Stop on BOTH entrypoints; exactly one
            // proves only the first caller ran the teardown body and the second short-circuited.
            infraReceiver.CallLog.Count(c => c == ReceiverCall.Stop)
                .Should().Be(1, "repeated StopReceiver must be single-flight and stop the infrastructure receiver exactly once");
        }

        // ------------------------------------------------------------------ (f) concurrent StopReceiver + DisposeAsync is single-flight

        [Fact]
        public async Task MustQuiesceOnceWhenStopReceiverAndDisposeAsyncRaceConcurrently()
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

            // Release the gate, then race StopReceiver and DisposeAsync at the same teardown: the single-flight guard
            // must let exactly one caller run the drain+infra-teardown while the other observes its completion via the
            // shared TaskCompletionSource. Neither may surface an ObjectDisposedException over the Exchange-and-disposed
            // token source/semaphore.
            releaseGate.Release(messageCount);
            var race = Task.WhenAll(sut.StopReceiver(), sut.DisposeAsync().AsTask());
            var completed = await Task.WhenAny(race, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(race, "concurrent StopReceiver and DisposeAsync must both quiesce and complete cleanly");
            await race; // surface any ObjectDisposedException from either entrypoint

            Volatile.Read(ref inFlight.Value).Should().Be(0, "all in-flight workers must have quiesced before the racing teardowns return");

            // Which caller wins admission is nondeterministic, so the divergent per-entrypoint infra call is either
            // Stop (StopReceiver won) OR Dispose (DisposeAsync won) — never both. The single-flight witness is that the
            // SUM of the two divergent teardown calls is exactly one: only the admitted caller ran a teardown body and
            // the loser observed its completion via the shared TaskCompletionSource without re-running infra teardown.
            var snapshot = infraReceiver.CallLog;
            var divergentTeardownCalls = snapshot.Count(c => c == ReceiverCall.Stop || c == ReceiverCall.Dispose);
            divergentTeardownCalls.Should().Be(1, "exactly one racing entrypoint may run the per-entrypoint infrastructure teardown (Stop or Dispose), proving single-flight admission");
        }
    }
}
