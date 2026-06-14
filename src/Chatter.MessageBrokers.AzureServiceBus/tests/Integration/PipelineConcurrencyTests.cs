using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Concurrency coverage driven THROUGH Chatter's real pipeline. The SYSTEM UNDER TEST is
    // BrokeredMessageReceiver's bounded-parallel receive loop: with a global MaxConcurrentCalls = N (> 1), the
    // loop admits up to N concurrent per-message workers via its SemaphoreSlim(N) gate, so up to N handler
    // invocations run SIMULTANEOUSLY. M > N latch-blocked messages are sent; the coordinator records the peak
    // number of handlers in-flight at once and the test asserts that peak == N (not 1, not M).
    //
    // The blocking handler + shared coordinator are registered into Chatter's REAL DI graph via the harness's
    // additive configureServices hook, so Chatter's command dispatcher resolves and invokes THIS handler on the
    // genuine receive path (a later IMessageHandler<TMessage> registration wins over the harness's default
    // RecordingMessageHandler). Raw Azure.Messaging.ServiceBus never appears: send is via
    // IBrokeredMessageDispatcher and receipt is via Chatter's pump.
    //
    // Regression fail-fast: a regressed SERIAL implementation would only ever have one handler in flight, so the
    // coordinator's "N arrived concurrently" signal never completes. The test awaits that signal with a BOUNDED
    // timeout and fails fast via TimeoutException instead of hanging CI. Once N are observed in flight the latch
    // is released so all M handlers complete and every PeekLock message is settled (Complete) — nothing leaks
    // onto the shared emulator queue.
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineConcurrencyTests
    {
        // Dedicated emulator queue for this class. MaxDeliveryCount is 10 so the broker does not deadletter
        // before the handlers are released; LockDuration (PT10S) comfortably covers the time the handlers stay
        // latched waiting for the concurrency signal.
        private const string ConcurrencyQueue = "chatter.concurrency";

        // The global concurrency cap under test (> 1) and the number of messages sent (M > N), so the loop must
        // hold more messages than it can process at once — proving the gate admits exactly N, not 1 and not M.
        private const int MaxConcurrentCalls = 3;
        private const int MessageCount = 5;

        // Bounded wait for N handlers to be in flight simultaneously. Generous for the slow emulator, where a
        // full integration run takes minutes, but finite so a regressed serial loop (only ever 1 in flight) fails
        // fast via TimeoutException rather than hanging CI.
        private static readonly TimeSpan ConcurrencyReachedWait = TimeSpan.FromSeconds(90);

        // Bounded settle window after the latch releases: long enough for all M PeekLock messages to be completed
        // so none leak onto the shared queue, finite so a stuck settle does not hang the run.
        private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(30);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineConcurrencyTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        public sealed class ConcurrencyCommand : ICommand
        {
            public string Value { get; set; }
        }

        // Shared coordinator the latched handler reports through. Tracks the live in-handler count and the peak
        // observed simultaneously, completes ConcurrencyReached once 'target' handlers are in flight at once, and
        // holds a release latch every handler awaits so the loop is forced to accumulate concurrent workers
        // rather than draining them one at a time.
        private sealed class ConcurrencyCoordinator
        {
            private readonly int _target;
            private readonly TaskCompletionSource<bool> _concurrencyReached =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _current;
            private int _peak;
            private int _completed;

            public ConcurrencyCoordinator(int target)
                => _target = target;

            // Completes once _target handlers are simultaneously in flight. The test awaits this (bounded) and a
            // serial regression never completes it, so the bounded wait fails fast.
            public Task ConcurrencyReached => _concurrencyReached.Task;

            // The highest number of handlers observed in flight at the same instant.
            public int PeakConcurrency => Volatile.Read(ref _peak);

            // The number of handler invocations that have fully completed (entered and exited). Lets the test
            // observe that every message settled through THIS handler after the latch released, without depending
            // on the harness's RecordingMessageHandler (which this handler replaces for the message type).
            public int CompletedCount => Volatile.Read(ref _completed);

            // Records entry into the handler, updates the peak, and signals when the target concurrency is hit.
            public void Enter()
            {
                var live = Interlocked.Increment(ref _current);

                // INVARIANT: monotonically raise the recorded peak to the highest live count any handler observed.
                // CompareExchange-loop so concurrent Enter calls cannot lose a higher observation to a race.
                int observedPeak;
                while (live > (observedPeak = Volatile.Read(ref _peak)))
                {
                    Interlocked.CompareExchange(ref _peak, live, observedPeak);
                }

                if (live >= _target)
                {
                    _concurrencyReached.TrySetResult(true);
                }
            }

            public void Exit()
            {
                Interlocked.Decrement(ref _current);
                Interlocked.Increment(ref _completed);
            }

            // Bounded poll until at least minCount handler invocations have completed, returning the observed
            // count (which may be below minCount if the timeout elapses — the caller asserts on it so a
            // never-reached threshold fails fast rather than hanging).
            public async Task<int> WaitForCompletedAsync(int minCount, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (CompletedCount >= minCount)
                    {
                        return CompletedCount;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }

                return CompletedCount;
            }

            // Releases every latched handler so all messages settle and the receiver can drain.
            public void Release()
                => _release.TrySetResult(true);

            // Awaited by each handler. Bounded so a never-released latch (e.g. concurrency target never reached)
            // cannot hang a handler — it throws TimeoutException, failing the test fast.
            public async Task WaitForRelease(TimeSpan timeout)
            {
                var completed = await Task.WhenAny(_release.Task, Task.Delay(timeout)).ConfigureAwait(false);
                if (completed != _release.Task)
                {
                    throw new TimeoutException(
                        $"Timed out after {timeout} waiting for the concurrency latch to be released.");
                }

                await _release.Task.ConfigureAwait(false);
            }
        }

        // The latched handler Chatter resolves on the receive path. Enters the coordinator (raising the observed
        // peak), then blocks on the release latch so concurrent invocations pile up to the SemaphoreSlim cap
        // instead of completing one at a time.
        private sealed class LatchedConcurrencyHandler : IMessageHandler<ConcurrencyCommand>
        {
            private readonly ConcurrencyCoordinator _coordinator;

            public LatchedConcurrencyHandler(ConcurrencyCoordinator coordinator)
                => _coordinator = coordinator;

            public async Task Handle(ConcurrencyCommand message, IMessageHandlerContext context)
            {
                _coordinator.Enter();
                try
                {
                    await _coordinator.WaitForRelease(ConcurrencyReachedWait).ConfigureAwait(false);
                }
                finally
                {
                    _coordinator.Exit();
                }
            }
        }

        // With MaxConcurrentCalls = N and M > N latch-blocked messages, exactly N handlers must run at once: the
        // receive loop's SemaphoreSlim(N) admits N concurrent workers, the M-N surplus waits, and the recorded
        // peak settles at N. A serial regression would peak at 1 and the concurrency signal would never fire
        // (bounded TimeoutException). A peak above N would mean the cap leaked.
        [RequiresDockerFact]
        public async Task GlobalMaxConcurrentCallsRunsExactlyThatManyHandlersAtOnce()
        {
            var coordinator = new ConcurrencyCoordinator(MaxConcurrentCalls);

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    sb.WithMaxConcurrentCalls(MaxConcurrentCalls);
                    sb.AddQueueReceiver<ConcurrencyCommand>(ConcurrencyQueue, transactionMode: TransactionMode.ReceiveOnly);
                },
                services =>
                {
                    // Registered AFTER the harness's default RecordingMessageHandler<ConcurrencyCommand>, so this
                    // coordinator-driven handler wins on GetRequiredService and is the one Chatter invokes.
                    services.AddSingleton(coordinator);
                    services.AddTransient<IMessageHandler<ConcurrencyCommand>, LatchedConcurrencyHandler>();
                },
                typeof(ConcurrencyCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                for (var i = 0; i < MessageCount; i++)
                {
                    await dispatcher.Send(new ConcurrencyCommand { Value = $"message-{i}" }, ConcurrencyQueue);
                }
            }

            // Bounded: a serial regression never reaches N concurrent handlers, so this throws TimeoutException
            // and the test fails fast instead of hanging.
            var concurrencyReached = await Task.WhenAny(
                coordinator.ConcurrencyReached,
                Task.Delay(ConcurrencyReachedWait));
            (concurrencyReached == coordinator.ConcurrencyReached).Should().BeTrue(
                $"the receive loop must run {MaxConcurrentCalls} handlers simultaneously when MaxConcurrentCalls is " +
                $"{MaxConcurrentCalls} and {MessageCount} messages are pending; a serial loop would only ever run one");

            // Release the latch so every handler completes and all PeekLock messages are settled (Complete), then
            // give the receiver a bounded window to drain so nothing leaks onto the shared emulator queue.
            coordinator.Release();
            var completed = await coordinator.WaitForCompletedAsync(MessageCount, SettleWait);
            completed.Should().Be(
                MessageCount,
                $"after the latch releases, all {MessageCount} messages must flow through the handler and settle");

            coordinator.PeakConcurrency.Should().Be(
                MaxConcurrentCalls,
                $"the global MaxConcurrentCalls = {MaxConcurrentCalls} must cap simultaneous handler invocations at " +
                $"exactly {MaxConcurrentCalls} — never 1 (serial regression) and never {MessageCount} (cap leaked)");
        }
    }
}
