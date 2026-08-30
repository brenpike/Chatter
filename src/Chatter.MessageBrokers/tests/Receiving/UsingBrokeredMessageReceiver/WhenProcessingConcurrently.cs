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
    // INVARIANT: proves the STEP-001 parallel receive loop actually fans out per-message processing up to
    // MaxConcurrentCalls concurrently, WITHOUT a real broker. The receive call stays serialized on the loop
    // thread; each non-null received message spawns a tracked worker gated by the MaxConcurrentCalls-sized
    // SemaphoreSlim. The concurrency is observed by gating the dispatcher (the processing path) so every worker
    // blocks inside DispatchAsync while an interlocked counter records the peak number simultaneously in flight.
    public class WhenProcessingConcurrently : Testing.Core.Context
    {
        // INVARIANT: MessageBrokerOptions.TransactionMode has internal set; accessible via InternalsVisibleTo("Chatter.MessageBrokers.Tests").
        private static MessageBrokerOptions BuildOptions(TransactionMode mode = TransactionMode.None)
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = mode;
            return opts;
        }

        // INVARIANT: pass-through IRecoveryStrategy invokes the delegate exactly once, no retry machinery.
        // Mirrors WhenReceiving.PassThroughRecovery so the concurrency observed is the loop's, not the strategy's.
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

        // INVARIANT: body must deserialise cleanly as FakeMessage; each context gets a unique message id so the
        // CallLog Ack count maps one-to-one to processed messages.
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

        // INVARIANT: polls the CallLog (a locked snapshot) until at least the expected number of Acks land, then
        // returns. The watchdog bounds the wait so a regression that never acks fails fast instead of hanging.
        private static async Task WaitForAckCountAsync(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            int expectedAckCount,
            CancellationToken watchdog)
        {
            while (infraReceiver.CallLog.Count(c => c == ReceiverCall.Ack) < expectedAckCount)
            {
                watchdog.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        // INVARIANT: spins until the predicate holds or the watchdog fires. Used to wait for the in-flight worker
        // count to reach the concurrency cap. A regressed (sequential) implementation never reaches a peak > 1, so
        // the bounded watchdog converts that regression into a prompt OperationCanceledException rather than a hang.
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

        // ------------------------------------------------------------------ (1) bounded concurrency: peak in-flight == N

        [Fact]
        public async Task MustRunUpToMaxConcurrentCallsProcessorsSimultaneously()
        {
            const int maxConcurrentCalls = 4;
            const int messageCount = 10; // > N so the semaphore cap is genuinely reached and (N+1)th worker is blocked

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // Gate every worker inside DispatchAsync: increment the in-flight counter, record the peak, then block on
            // the release gate. With the cap held by N blocked workers, the (N+1)th semaphore WaitAsync cannot admit
            // another worker, so the peak settles at exactly N until the gate is released.
            var inFlight = 0;
            var peakInFlight = 0;
            using var releaseGate = new SemaphoreSlim(0, messageCount);

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    var current = Interlocked.Increment(ref inFlight);
                    // Record the running maximum without losing concurrent updates.
                    int observedPeak;
                    do
                    {
                        observedPeak = Volatile.Read(ref peakInFlight);
                        if (current <= observedPeak) break;
                    }
                    while (Interlocked.CompareExchange(ref peakInFlight, current, observedPeak) != observedPeak);

                    try
                    {
                        await releaseGate.WaitAsync(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight);
                    }
                });

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Wait until exactly N workers are simultaneously blocked inside DispatchAsync. Because all N are held,
            // the loop's (N+1)th WaitAsync is blocked and in-flight cannot exceed N.
            await WaitUntilAsync(() => Volatile.Read(ref inFlight) == maxConcurrentCalls, watchdog.Token);

            // Give any erroneously-admitted extra worker a chance to surface before asserting the cap. If the loop
            // ever over-admitted, peakInFlight would exceed N here.
            await Task.Yield();
            Volatile.Read(ref peakInFlight).Should().Be(maxConcurrentCalls,
                because: "the MaxConcurrentCalls-sized semaphore must admit exactly N processors at once");

            // Release all gated workers so every message completes and is acked.
            releaseGate.Release(messageCount);

            await WaitForAckCountAsync(infraReceiver, messageCount, watchdog.Token);

            cts.Cancel();
            await loop;

            Volatile.Read(ref peakInFlight).Should().Be(maxConcurrentCalls,
                because: "peak simultaneous processors must equal MaxConcurrentCalls, never more");
            infraReceiver.CallLog.Count(c => c == ReceiverCall.Ack).Should().Be(messageCount,
                because: "every received message must ultimately be acked");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter);
        }

        // ------------------------------------------------------------------ (2) MaxConcurrentCalls == 1 stays sequential

        [Fact]
        public async Task MustProcessSequentiallyWhenMaxConcurrentCallsIsOne()
        {
            const int messageCount = 5;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // No release gate: each worker increments, records the peak, briefly yields, then decrements and returns.
            // With MaxConcurrentCalls == 1 the semaphore admits one worker at a time, so the observed peak must
            // never exceed 1. A regression that fans out unbounded would record a peak > 1 here.
            var inFlight = 0;
            var peakInFlight = 0;

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    var current = Interlocked.Increment(ref inFlight);
                    int observedPeak;
                    do
                    {
                        observedPeak = Volatile.Read(ref peakInFlight);
                        if (current <= observedPeak) break;
                    }
                    while (Interlocked.CompareExchange(ref peakInFlight, current, observedPeak) != observedPeak);

                    // Yield so a buggy fan-out would have a real chance to overlap a second worker here.
                    await Task.Yield();
                    Interlocked.Decrement(ref inFlight);
                });

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls: 1), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitForAckCountAsync(infraReceiver, messageCount, watchdog.Token);

            cts.Cancel();
            await loop;

            Volatile.Read(ref peakInFlight).Should().Be(1,
                because: "MaxConcurrentCalls == 1 must keep processing strictly sequential (at most one in flight)");
            infraReceiver.CallLog.Count(c => c == ReceiverCall.Ack).Should().Be(messageCount,
                because: "every received message must be acked even in the sequential path");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Nack);
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter);
        }

        // ------------------------------------------------------------------ (3) clean drain/stop on cancellation

        [Fact]
        public async Task MustDrainInFlightWorkersAndStopCleanlyOnCancellation()
        {
            const int maxConcurrentCalls = 3;
            const int messageCount = 8;

            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: messageCount);
            for (var i = 0; i < messageCount; i++)
                infraReceiver.Enqueue(BuildContext());

            // Hold workers inside DispatchAsync until the test has cancelled, proving the loop drains the live
            // worker set on shutdown rather than tearing down underneath in-flight processing. Each worker observes
            // the release gate; on cancellation the gate's WaitAsync throws OCE and the worker's ladder swallows it
            // under the receiver token, so no Nack/Deadletter is recorded for the gated messages.
            var inFlight = 0;
            using var releaseGate = new SemaphoreSlim(0, messageCount);

            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (FakeMessage _, MessageBrokerContext __, CancellationToken token) =>
                {
                    Interlocked.Increment(ref inFlight);
                    try
                    {
                        await releaseGate.WaitAsync(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight);
                    }
                });

            var sut = CreateSut(infraReceiver, dispatcher);

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token));

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Wait until the cap is fully occupied by blocked workers, then cancel while they are still in flight.
            await WaitUntilAsync(() => Volatile.Read(ref inFlight) == maxConcurrentCalls, watchdog.Token);

            cts.Cancel();

            // The loop must complete (drain to quiescence) rather than hang or fault. Bound the await so a failure
            // to drain surfaces as a TimeoutException instead of hanging the run.
            var completed = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15)));
            completed.Should().BeSameAs(loop, "the receive loop must drain in-flight workers and stop cleanly on cancellation");
            await loop; // surface any fault from the loop itself

            Volatile.Read(ref inFlight).Should().Be(0,
                because: "all in-flight workers must have quiesced once the loop has drained and stopped");
        }
    }
}
