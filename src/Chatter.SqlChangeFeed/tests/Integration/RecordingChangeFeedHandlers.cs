using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Context;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Shared, harness-owned signal for one change-feed event type. Registered as a singleton so the single
    // instance is visible to BOTH the test (which awaits Handled / polls InvocationCount) and the DI-resolved
    // RecordingChangeFeedHandler<TEvent> (which Chatter constructs transiently per dispatch and which signals
    // through this registry). Captures the deserialized event payload so the test can assert on what the
    // production change-feed path materialized from the row change. Mirrors the SQL Service Broker integration
    // HandlerSignal<T>.
    public sealed class ChangeFeedSignal<TEvent>
    {
        private readonly TaskCompletionSource<TEvent> _handled =
            new TaskCompletionSource<TEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<TEvent> _records = new ConcurrentQueue<TEvent>();
        private int _invocationCount;

        public Task<TEvent> Handled => _handled.Task;

        // The total number of times Chatter's pipeline has dispatched TEvent to the handler. Lets a test observe
        // re-arm (a second notification after the first) without holding the transiently-constructed handler.
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        // Every captured event in arrival order. Unlike Handled (which holds only the FIRST event), this lets a
        // test assert on the PAYLOAD of a later delivery rather than inferring it from the invocation count alone.
        public IReadOnlyCollection<TEvent> Records => _records;

        internal void Record(TEvent @event)
        {
            Interlocked.Increment(ref _invocationCount);
            _records.Enqueue(@event);
            _handled.TrySetResult(@event);
        }
    }

    // Registry of per-event-type signals, registered as a singleton. The harness creates/looks up the
    // ChangeFeedSignal<TEvent> for an event type; the DI-resolved RecordingChangeFeedHandler<TEvent> resolves
    // this same registry and reports its capture through it. Keying by Type keeps the test's expectation and the
    // handler instance pointed at one shared signal without the test holding the handler instance (Chatter owns
    // construction). Also exposes bounded waits that throw on timeout so a stalled receive fails fast instead of
    // hanging CI (the change-feed receiver inherits the SSB WAITFOR-hang risk).
    public sealed class ChangeFeedSignalRegistry
    {
        private readonly ConcurrentDictionary<Type, object> _signals = new ConcurrentDictionary<Type, object>();

        public ChangeFeedSignal<TEvent> GetOrAdd<TEvent>()
            => (ChangeFeedSignal<TEvent>)_signals.GetOrAdd(typeof(TEvent), _ => new ChangeFeedSignal<TEvent>());

        // Bounded wait for the FIRST dispatch of TEvent: returns the captured event, or throws TimeoutException
        // so a stalled receive fails fast instead of hanging. NEVER an unbounded wait.
        public async Task<TEvent> WaitForHandledAsync<TEvent>(TimeSpan timeout)
        {
            var handled = GetOrAdd<TEvent>().Handled;
            var completed = await Task.WhenAny(handled, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != handled)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for a change-feed dispatch of '{typeof(TEvent).Name}'.");
            }

            return await handled.ConfigureAwait(false);
        }

        // Bounded poll until TEvent has been dispatched at least minCount times, then returns the observed count.
        // Throws TimeoutException if the threshold is not reached before the timeout, so a never-reached count
        // fails fast. NEVER an unbounded wait.
        public async Task<int> WaitForInvocationCountAsync<TEvent>(int minCount, TimeSpan timeout)
        {
            var signal = GetOrAdd<TEvent>();
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (signal.InvocationCount >= minCount)
                {
                    return signal.InvocationCount;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }

            if (signal.InvocationCount < minCount)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for '{typeof(TEvent).Name}' to be dispatched at least " +
                    $"{minCount} time(s); observed {signal.InvocationCount}.");
            }

            return signal.InvocationCount;
        }
    }

    // A Chatter IMessageHandler<TEvent> that records the dispatched change-feed event and signals the shared
    // ChangeFeedSignal<TEvent> from the registry. Registered explicitly by ChatterChangeFeedHarness as a CLOSED
    // IMessageHandler<TEvent> per event type (Chatter's assembly scan excludes open generics), so it participates
    // in the real change-feed dispatch path ChangeFeedReceiver drives. Mirrors the SQL Service Broker integration
    // RecordingMessageHandler<T>.
    public sealed class RecordingChangeFeedHandler<TEvent> : IMessageHandler<TEvent> where TEvent : IMessage
    {
        private readonly ChangeFeedSignalRegistry _registry;

        public RecordingChangeFeedHandler(ChangeFeedSignalRegistry registry)
            => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        public Task Handle(TEvent message, IMessageHandlerContext context)
        {
            _registry.GetOrAdd<TEvent>().Record(message);
            return Task.CompletedTask;
        }
    }
}
