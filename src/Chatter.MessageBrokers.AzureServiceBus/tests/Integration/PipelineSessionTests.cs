using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Session coverage driven THROUGH Chatter's real pipeline against a session-enabled emulator queue
    // (Config.json "chatter.session" with RequiresSession=true). The SYSTEM UNDER TEST is Chatter's session
    // adapter behind the existing IServiceBusMessageReceiver port plus the held-session-receiver surfaced into
    // the handler context: a session-mode receiver (AddSessionQueueReceiver) accepts ONE session at a time,
    // serves its messages FIFO through Chatter's pull pump, and exposes durable per-session state via the
    // held ServiceBusSessionReceiver. The SessionId is set on the send side as the Group Id
    // (SendOptions.WithGroupId, which the existing mapping forwards to ServiceBusMessage.SessionId) — no
    // WithSessionId alias exists. Raw Azure.Messaging.ServiceBus never appears as the system under test: send
    // is via IBrokeredMessageDispatcher and receipt is via Chatter's pump and handler context.
    //
    // EMULATOR SESSION SUPPORT: the Azure Service Bus emulator supports session-enabled entities
    // (RequiresSession=true) and the message-session APIs (accept-next-session, session state). These facts
    // are still gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    //
    // MaxConcurrentCalls means CONCURRENT SESSIONS for a session-mode receiver (ADR-0014): a receiver at N
    // holds up to N sessions at once through a multiplexer over N single-session children, and each held
    // session is still served ONE message at a time. The session-state and single-session-FIFO facts below
    // register at N = 1 (WithMaxConcurrentCalls(1)), where the bare single-session adapter is used with no
    // multiplexer in the path, so they observe the order Chatter handed messages to the handler and not a race
    // in the test's own observation. The facts after them register at N = 3 and pin what N buys: three sessions
    // held simultaneously with three DISTINCT Group Ids in flight, FIFO still intact within one session while
    // the receiver is sized for three, and a per-receiver value of 1 beating a global of 3 at runtime.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineSessionTests
    {
        private const string SessionQueue = "chatter.session";

        // Dedicated session-enabled emulator queues for the multiplexed facts, so a receiver holding N sessions
        // never competes for sessions with another fact's messages on a shared entity. INVARIANT: every name
        // here has a queue entry of the same name with RequiresSession=true in Integration/Config.json — the
        // emulator provisions entities declaratively and rejects receivers for entities it was never told about.
        private const string SessionConcurrencyQueue = "chatter.session.multi";
        private const string SessionFifoQueue = "chatter.session.fifo";
        private const string SessionPerReceiverQueue = "chatter.session.perreceiver";

        // The per-receiver session concurrency under test (> 1) and the number of DISTINCT sessions seeded
        // (> N), so more sessions are available than the receiver may hold — proving it holds exactly N.
        private const int MaxConcurrentSessions = 3;
        private const int SeededSessionCount = 5;

        // The sequence number of the message whose handler is deliberately slow in the FIFO fact.
        private const int SlowHeadSequence = 0;

        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(60);

        // Bounded wait for N handlers to be in flight simultaneously. Generous for the slow emulator but finite,
        // so a receiver that only ever holds one session fails fast instead of hanging CI.
        private static readonly TimeSpan ConcurrencyReachedWait = TimeSpan.FromSeconds(90);

        // Bounded settle window after the held deliveries are released: long enough for every PeekLock message
        // to be completed so none leak onto the emulator queue, finite so a stuck settle does not hang the run.
        private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(30);

        // How long a handler holds its delivery when the concurrency target it watches for is never expected to
        // be reached. It is the window in which a second handler WOULD be observed if the receiver ran one, so
        // the per-receiver-override fact can assert a peak of exactly 1 without waiting on a signal that must
        // never fire.
        private static readonly TimeSpan OverlapWindow = TimeSpan.FromSeconds(2);

        // How long a held session may yield nothing before the receiver releases it and accepts the NEXT session.
        // The facts that seed several single-message sessions spend one rollover per session drained, and the
        // 60-second default (ServiceBusOptions.SessionIdleTimeout) would make each of those drains take a minute.
        // The rollover path itself is unchanged — it just comes round sooner.
        private static readonly TimeSpan SessionIdleRollover = TimeSpan.FromSeconds(2);

        // How long the FIRST message's handler sleeps in the FIFO fact. Long enough that a multiplexer pulling a
        // second message from the busy session would deterministically finish a later message first; well inside
        // the queue's PT10S lock duration.
        private static readonly TimeSpan SlowHeadDelay = TimeSpan.FromSeconds(2);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineSessionTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        // A command carrying a monotonically increasing sequence number so the FIFO assertion can compare the
        // order Chatter delivered messages to the handler against the order they were sent.
        public sealed class SessionOrderedCommand : ICommand
        {
            public int Sequence { get; set; }
        }

        // A command that drives the session-state round-trip: each message instructs the handler what to read
        // and write on the held session's durable state, and the handler records what it observed BEFORE its
        // own write so the test can assert state set on an earlier message is visible to a later one.
        public sealed class SessionStateCommand : ICommand
        {
            public int Step { get; set; }
        }

        // A command for the multiplexed-concurrency facts. It carries nothing the handler needs: those facts
        // read the delivery's Group Id off the inbound broker context, which is what identifies the SESSION the
        // delivery came from.
        public sealed class SessionConcurrencyCommand : ICommand
        {
            public string Value { get; set; }
        }

        // FIFO within a single session: N commands published with the SAME SessionId (Group Id) must be handled
        // in send order. With concurrency pinned to 1 and a single held session served one-at-a-time, the
        // recorder's arrival-ordered Records must equal the sent sequence 0..N-1. A regression that broke
        // session FIFO ordering (or served multiple sessions/messages out of order) would produce a different
        // ordering and fail this assertion.
        [RequiresDockerFact]
        public async Task MessagesInOneSessionAreHandledInSendOrder()
        {
            const int messageCount = 5;
            var sessionId = Guid.NewGuid().ToString();

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    // Pin concurrency to 1 so the pump cannot interleave two messages from the held session;
                    // FIFO is then the order Chatter handed messages to the handler.
                    sb.WithMaxConcurrentCalls(1);
                    sb.AddSessionQueueReceiver<SessionOrderedCommand>(SessionQueue);
                },
                typeof(SessionOrderedCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                for (var i = 0; i < messageCount; i++)
                {
                    await dispatcher.Send(new SessionOrderedCommand { Sequence = i }, SessionQueue, options: SessionSend(sessionId));
                }
            }

            var signal = harness.GetSignal<SessionOrderedCommand>();
            var observed = await WaitForRecordCountAsync(signal, messageCount, HandlerWait);
            observed.Should().Be(
                messageCount,
                $"all {messageCount} messages sent to the one session must be delivered through Chatter's pump");

            var deliveredOrder = signal.Records.Select(record => record.Message.Sequence).ToList();
            deliveredOrder.Should().Equal(
                Enumerable.Range(0, messageCount),
                "messages within a single Azure Service Bus session must be handled FIFO in send order");

            // The SessionId set as Group Id on send is surfaced back onto the inbound context's Group Id header.
            var firstContext = signal.Records.First().Context;
            firstContext.BrokeredMessage.MessageContext.Should().ContainKey(MessageContext.GroupId);
            firstContext.BrokeredMessage.MessageContext[MessageContext.GroupId].Should().Be(
                sessionId, "the inbound message's Group Id must carry the SessionId the message was sent with");
        }

        // Session-state round-trip across messages in ONE session: the handler reads the held session's durable
        // state at the start of each message and writes new state before returning. Message 1 sees no prior
        // state and sets "state-1"; message 2 sees "state-1" (proving durable per-session state persisted across
        // messages via the held session receiver) and then CLEARS it; message 3 sees no state again (proving
        // Clear took effect). This exercises Get/Set/Clear against the real held ServiceBusSessionReceiver.
        [RequiresDockerFact]
        public async Task SessionStateRoundTripsAcrossMessagesInOneSession()
        {
            const int messageCount = 3;
            var sessionId = Guid.NewGuid().ToString();
            var observer = new SessionStateObserver();

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    // One-at-a-time handling so each message's read observes the previous message's write, not a
                    // concurrent in-flight write.
                    sb.WithMaxConcurrentCalls(1);
                    sb.AddSessionQueueReceiver<SessionStateCommand>(SessionQueue);
                },
                services =>
                {
                    // Registered AFTER the harness's default RecordingMessageHandler<SessionStateCommand>, so this
                    // state-driving handler wins on GetRequiredService and is the one Chatter invokes.
                    services.AddSingleton(observer);
                    services.AddTransient<IMessageHandler<SessionStateCommand>, SessionStateHandler>();
                },
                typeof(SessionStateCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                for (var step = 1; step <= messageCount; step++)
                {
                    await dispatcher.Send(new SessionStateCommand { Step = step }, SessionQueue, options: SessionSend(sessionId));
                }
            }

            var handledSteps = await observer.WaitForHandledAsync(messageCount, HandlerWait);
            handledSteps.Should().Be(
                messageCount,
                $"all {messageCount} session-state messages must be delivered through Chatter's pump");

            // Step 1: no prior state (the session starts empty).
            observer.StateSeenAt(1).Should().BeNull(
                "the first message in a fresh session must observe no durable session state");

            // Step 2: the state SET on step 1 must be durably visible — this is the proof of per-session state
            // persisting across messages via the held session receiver.
            observer.StateSeenAt(2).Should().Be(
                "state-1", "session state set while handling an earlier message must persist to a later message in the same session");

            // Step 3: after step 2 CLEARED the state, it must be gone again — proving Clear took effect.
            observer.StateSeenAt(3).Should().BeNull(
                "clearing session state while handling a message must remove it for later messages in the same session");
        }

        // N sessions held at once: a session receiver registered with MaxConcurrentCalls = N holds N sessions
        // SIMULTANEOUSLY, and the N deliveries in flight come from N DISTINCT sessions. More sessions are seeded
        // than the receiver may hold, and every handler holds its delivery until N are in flight, so the receiver
        // must accumulate held sessions rather than draining them one at a time. The DISTINCTNESS assertion is the
        // one that matters: N concurrent handlers all drawn from ONE session would break FIFO per Group Id, which
        // is precisely what the multiplexer must never do.
        [RequiresDockerFact]
        public async Task SessionReceiverHoldsMaxConcurrentCallsSessionsAtOnce()
        {
            var coordinator = new SessionConcurrencyCoordinator(MaxConcurrentSessions, ConcurrencyReachedWait);
            var sessionIds = Enumerable.Range(0, SeededSessionCount)
                                       .Select(_ => Guid.NewGuid().ToString())
                                       .ToList();

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    sb.WithSessionIdleTimeout(SessionIdleRollover);
                    // Per-receiver: in session mode this is how many SESSIONS this receiver holds at once.
                    sb.AddSessionQueueReceiver<SessionConcurrencyCommand>(
                        SessionConcurrencyQueue, maxConcurrentCalls: MaxConcurrentSessions);
                },
                services =>
                {
                    // Registered AFTER the harness's default RecordingMessageHandler<SessionConcurrencyCommand>,
                    // so this coordinator-driven handler wins on GetRequiredService and is the one Chatter invokes.
                    services.AddSingleton(coordinator);
                    services.AddTransient<IMessageHandler<SessionConcurrencyCommand>, HeldSessionHandler>();
                },
                typeof(SessionConcurrencyCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                foreach (var sessionId in sessionIds)
                {
                    await dispatcher.Send(
                        new SessionConcurrencyCommand { Value = sessionId }, SessionConcurrencyQueue,
                        options: SessionSend(sessionId));
                }
            }

            // Bounded: a receiver that holds one session at a time never reaches N concurrent handlers, so this
            // elapses and the test fails fast instead of hanging.
            var concurrencyReached = await Task.WhenAny(
                coordinator.ConcurrencyReached, Task.Delay(ConcurrencyReachedWait));
            var reachedTarget = concurrencyReached == coordinator.ConcurrencyReached;
            var groupIdsInFlight = coordinator.GroupIdsAtPeak;

            // Release every held delivery so all messages settle (Complete) and nothing is left locked on the
            // emulator queue, whatever the assertions below decide.
            coordinator.Release();
            var completed = await coordinator.WaitForCompletedAsync(SeededSessionCount, SettleWait);

            reachedTarget.Should().BeTrue(
                $"a session receiver registered with MaxConcurrentCalls = {MaxConcurrentSessions} must hold " +
                $"{MaxConcurrentSessions} sessions at once when {SeededSessionCount} sessions are pending, but it " +
                $"peaked at {coordinator.PeakConcurrency}. SUSPECT THE EMULATOR FIRST: the Azure Service Bus " +
                "emulator is documented as intended for sequential testing, and this fact is the only coverage " +
                "here that depends on it granting one client several CONCURRENT SESSION LOCKS. If the emulator " +
                "refuses that, the product is not what broke — the multiplexer failing to arm its idle children " +
                "is the second suspect, not the first");

            groupIdsInFlight.Should().HaveCount(
                MaxConcurrentSessions,
                $"exactly {MaxConcurrentSessions} deliveries must be in flight when the receiver is holding " +
                $"{MaxConcurrentSessions} sessions");
            groupIdsInFlight.Distinct().Should().HaveCount(
                MaxConcurrentSessions,
                $"the {MaxConcurrentSessions} in-flight deliveries must come from {MaxConcurrentSessions} DISTINCT " +
                "sessions — concurrent handlers drawn from ONE session would be the FIFO-per-Group-Id break this " +
                "feature must not introduce");

            coordinator.PeakConcurrency.Should().Be(
                MaxConcurrentSessions,
                $"MaxConcurrentCalls = {MaxConcurrentSessions} must cap held sessions at exactly " +
                $"{MaxConcurrentSessions} — never 1 (the removed session clamp) and never {SeededSessionCount} " +
                "(the cap leaked)");
            completed.Should().Be(
                SeededSessionCount,
                $"after the held deliveries release, all {SeededSessionCount} messages must flow through the " +
                "handler and settle");
        }

        // FIFO WITHIN one session survives a receiver sized for many: five messages sent to ONE session are
        // handled in send order even though the receiver may hold three sessions. The FIRST message's handler
        // sleeps while the rest are fast, so the assertion has teeth — a multiplexer that pulled a second message
        // from a session it is already serving would finish a later message during that sleep and the recorded
        // order would start with a message other than the first. Completion order is what is recorded, not entry
        // order: an entry-order record would read as send order even under a concurrent pull, because the head
        // still ENTERS first — it just would not finish first.
        [RequiresDockerFact]
        public async Task MessagesInOneSessionStayInSendOrderWhileTheReceiverHoldsManySessions()
        {
            const int messageCount = 5;
            var sessionId = Guid.NewGuid().ToString();
            var observer = new SessionCompletionObserver();

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    // Sized for three sessions while only one exists: the per-session one-message-at-a-time
                    // guarantee must hold on its own, not because the receiver had no room to break it.
                    sb.AddSessionQueueReceiver<SessionOrderedCommand>(
                        SessionFifoQueue, maxConcurrentCalls: MaxConcurrentSessions);
                },
                services =>
                {
                    services.AddSingleton(observer);
                    services.AddTransient<IMessageHandler<SessionOrderedCommand>, SlowHeadSessionHandler>();
                },
                typeof(SessionOrderedCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                for (var i = 0; i < messageCount; i++)
                {
                    await dispatcher.Send(
                        new SessionOrderedCommand { Sequence = i }, SessionFifoQueue,
                        options: SessionSend(sessionId));
                }
            }

            var completed = await observer.WaitForCompletedAsync(messageCount, HandlerWait);
            completed.Should().Be(
                messageCount,
                $"all {messageCount} messages sent to the one session must be delivered through Chatter's pump");

            observer.CompletionOrder.Should().Equal(
                Enumerable.Range(0, messageCount),
                "a held session is served ONE message at a time, so messages within it must complete in send " +
                $"order even at MaxConcurrentCalls = {MaxConcurrentSessions}; a second message pulled from the " +
                "busy session would have finished while message 0's handler slept and appeared ahead of it");
        }

        // Specificity beats source at RUNTIME: a session receiver that states MaxConcurrentCalls = 1 holds exactly
        // one session even under a global of 3. Each handler holds its delivery for an overlap window, so a second
        // concurrent handler would be observed if the receiver ran one; the peak must stay at 1.
        [RequiresDockerFact]
        public async Task PerReceiverMaxConcurrentCallsOfOneBeatsTheGlobalAtRuntime()
        {
            const int sessionCount = 3;

            // Target 2 is the FIRST observation that would break the claim: reaching it releases every held
            // delivery immediately, so a receiver that ignored the per-receiver value fails fast rather than
            // spending the full overlap window per message.
            var coordinator = new SessionConcurrencyCoordinator(target: 2, hold: OverlapWindow);
            var sessionIds = Enumerable.Range(0, sessionCount)
                                       .Select(_ => Guid.NewGuid().ToString())
                                       .ToList();

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    sb.WithMaxConcurrentCalls(MaxConcurrentSessions);
                    sb.WithSessionIdleTimeout(SessionIdleRollover);
                    sb.AddSessionQueueReceiver<SessionConcurrencyCommand>(
                        SessionPerReceiverQueue, maxConcurrentCalls: 1);
                },
                services =>
                {
                    services.AddSingleton(coordinator);
                    services.AddTransient<IMessageHandler<SessionConcurrencyCommand>, HeldSessionHandler>();
                },
                typeof(SessionConcurrencyCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                foreach (var sessionId in sessionIds)
                {
                    await dispatcher.Send(
                        new SessionConcurrencyCommand { Value = sessionId }, SessionPerReceiverQueue,
                        options: SessionSend(sessionId));
                }
            }

            var completed = await coordinator.WaitForCompletedAsync(sessionCount, HandlerWait);
            coordinator.Release();

            completed.Should().Be(
                sessionCount,
                $"all {sessionCount} messages must be delivered through Chatter's pump, one session at a time");
            coordinator.PeakConcurrency.Should().Be(
                1,
                $"the receiver stated MaxConcurrentCalls = 1, so it must hold exactly one session at a time even " +
                $"though the global value is {MaxConcurrentSessions}; a peak above 1 means the global beat the " +
                "per-receiver value");
        }

        // Builds the SendOptions that route a message to the given session. SendOptions.WithGroupId stamps
        // MessageContext.GroupId, which the existing ASB mapping forwards to ServiceBusMessage.SessionId. The
        // mapping (OutboundBrokeredMessageExtensions.AsAzureServiceBusMessage) only assigns PartitionKey on the
        // SDK message when one was explicitly set, so a plain session send needs only the Group Id — no
        // PartitionKey is required. The matching-PartitionKey variant is pinned separately by the unit tests in
        // tests/Sending/UsingOutboundBrokeredMessageExtensions/WhenMappingToAzureServiceBusMessage.cs.
        private static SendOptions SessionSend(string sessionId)
            => new SendOptions().WithGroupId(sessionId);

        // Bounded poll until the recorder has captured at least minCount invocations, returning the observed
        // count (which may be below minCount if the timeout elapses — the caller asserts on it so a
        // never-reached threshold fails fast instead of hanging CI).
        private static async Task<int> WaitForRecordCountAsync<TMessage>(
            HandlerSignal<TMessage> signal, int minCount, TimeSpan timeout)
            where TMessage : IMessage
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (signal.Records.Count >= minCount)
                {
                    return signal.Records.Count;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }

            return signal.Records.Count;
        }

        // Shared observer the state-driving handler reports through: records, per step, the durable session
        // state the handler READ before performing its own write/clear, so the test can assert that state set
        // on an earlier message is visible to (or cleared for) a later message in the same session.
        private sealed class SessionStateObserver
        {
            private readonly ConcurrentDictionary<int, string> _stateSeen = new ConcurrentDictionary<int, string>();
            private int _handledCount;

            // Sentinel for "the handler read null session state at this step" so a recorded null is
            // distinguishable from "this step was never handled" (a missing key).
            private const string NullStateSentinel = "\0__null-session-state__";

            public void RecordStateSeen(int step, string stateSeen)
            {
                _stateSeen[step] = stateSeen ?? NullStateSentinel;
                System.Threading.Interlocked.Increment(ref _handledCount);
            }

            // The durable session state the handler observed at the start of the given step, or null if it read
            // no state. Throws if the step was never handled so a missed delivery surfaces clearly.
            public string StateSeenAt(int step)
            {
                if (!_stateSeen.TryGetValue(step, out var seen))
                {
                    throw new InvalidOperationException($"Step {step} was never handled.");
                }

                return seen == NullStateSentinel ? null : seen;
            }

            // Bounded poll until at least minCount steps have been handled, returning the observed count (which
            // may be below minCount on timeout — the caller asserts on it so a stalled receive fails fast).
            public async Task<int> WaitForHandledAsync(int minCount, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (System.Threading.Volatile.Read(ref _handledCount) >= minCount)
                    {
                        return System.Threading.Volatile.Read(ref _handledCount);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }

                return System.Threading.Volatile.Read(ref _handledCount);
            }
        }

        // The state-driving handler Chatter resolves on the session receive path. Each message reads the held
        // session's durable state (Get), records what it saw, then writes the next state (Set) or clears it
        // (Clear) depending on its step, exercising the full Get/Set/Clear surface over the held session.
        private sealed class SessionStateHandler : IMessageHandler<SessionStateCommand>
        {
            private readonly SessionStateObserver _observer;

            public SessionStateHandler(SessionStateObserver observer)
                => _observer = observer;

            public async Task Handle(SessionStateCommand message, IMessageHandlerContext context)
            {
                var existing = await context.GetSessionStateAsync().ConfigureAwait(false);
                _observer.RecordStateSeen(message.Step, existing is null ? null : existing.ToString());

                if (message.Step == 1)
                {
                    await context.SetSessionStateAsync(BinaryData.FromString("state-1")).ConfigureAwait(false);
                }
                else if (message.Step == 2)
                {
                    await context.ClearSessionStateAsync().ConfigureAwait(false);
                }
            }
        }

        // The Group Id of the delivery being handled — the Azure Service Bus SessionId the message was sent with,
        // surfaced onto the inbound broker context by the receive path. It is what identifies WHICH session a
        // concurrent delivery came from.
        private static string GroupIdOf(IMessageHandlerContext context)
        {
            var brokerContext = (IMessageBrokerContext)context;
            return brokerContext.BrokeredMessage.MessageContext[MessageContext.GroupId]?.ToString();
        }

        // Shared coordinator the holding handler reports through. Tracks the Group Ids of the deliveries in
        // flight, the peak number held at once and the Group Ids observed AT that peak, completes
        // ConcurrencyReached once 'target' deliveries are in flight simultaneously, and holds each handler until
        // the target is reached or the hold window elapses — so the receiver accumulates held sessions instead of
        // draining them one at a time.
        private sealed class SessionConcurrencyCoordinator
        {
            private readonly int _target;
            private readonly TimeSpan _hold;
            private readonly object _gate = new object();

            // INVARIANT: a LIST, not a set. Two concurrent deliveries from the SAME session is the defect the
            // distinctness assertion exists to catch, and a set would collapse them into one entry and hide it.
            private readonly List<string> _inFlightGroupIds = new List<string>();

            private readonly TaskCompletionSource<bool> _concurrencyReached =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private IReadOnlyList<string> _groupIdsAtPeak = Array.Empty<string>();
            private int _peak;
            private int _completed;

            public SessionConcurrencyCoordinator(int target, TimeSpan hold)
            {
                _target = target;
                _hold = hold;
            }

            // Completes once _target deliveries are simultaneously in flight. A receiver holding one session at a
            // time never completes it, so a bounded wait on it fails fast.
            public Task ConcurrencyReached => _concurrencyReached.Task;

            // The highest number of deliveries observed in flight at the same instant.
            public int PeakConcurrency
            {
                get { lock (_gate) { return _peak; } }
            }

            // The Group Ids of the deliveries in flight at the instant the peak was reached — the evidence that
            // the concurrent deliveries came from distinct sessions.
            public IReadOnlyList<string> GroupIdsAtPeak
            {
                get { lock (_gate) { return _groupIdsAtPeak; } }
            }

            // Handler invocations that have fully completed (entered and exited), so a test can observe that every
            // message settled through THIS handler.
            public int CompletedCount => Volatile.Read(ref _completed);

            public void Enter(string groupId)
            {
                lock (_gate)
                {
                    _inFlightGroupIds.Add(groupId);

                    if (_inFlightGroupIds.Count > _peak)
                    {
                        _peak = _inFlightGroupIds.Count;
                        _groupIdsAtPeak = _inFlightGroupIds.ToArray();
                    }

                    if (_inFlightGroupIds.Count >= _target)
                    {
                        _concurrencyReached.TrySetResult(true);
                    }
                }
            }

            public void Exit(string groupId)
            {
                lock (_gate)
                {
                    _inFlightGroupIds.Remove(groupId);
                }

                Interlocked.Increment(ref _completed);
            }

            // Releases every held delivery so all messages settle and the receiver can drain.
            public void Release()
                => _release.TrySetResult(true);

            // Awaited by each handler: holds the delivery until the target concurrency is observed or the hold
            // window elapses, whichever comes first. Bounded by construction, so a fact whose target is never
            // meant to be reached cannot hang a handler.
            public async Task HoldAsync()
                => await Task.WhenAny(_release.Task, Task.Delay(_hold)).ConfigureAwait(false);

            // Bounded poll until at least minCount handler invocations have completed, returning the observed
            // count (which may be below minCount on timeout — the caller asserts on it so a never-reached
            // threshold fails fast instead of hanging).
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
        }

        // The holding handler Chatter resolves on the session receive path. Records the Group Id of the delivery
        // it is serving, then holds it so concurrent deliveries pile up to whatever the receiver actually allows.
        private sealed class HeldSessionHandler : IMessageHandler<SessionConcurrencyCommand>
        {
            private readonly SessionConcurrencyCoordinator _coordinator;

            public HeldSessionHandler(SessionConcurrencyCoordinator coordinator)
                => _coordinator = coordinator;

            public async Task Handle(SessionConcurrencyCommand message, IMessageHandlerContext context)
            {
                var groupId = GroupIdOf(context);
                _coordinator.Enter(groupId);
                try
                {
                    await _coordinator.HoldAsync().ConfigureAwait(false);
                }
                finally
                {
                    _coordinator.Exit(groupId);
                }
            }
        }

        // Records the order in which handling of each message COMPLETED. ConcurrentQueue enumerates in enqueue
        // order, so the recorded sequence is the completion order the FIFO assertion compares against send order.
        private sealed class SessionCompletionObserver
        {
            private readonly ConcurrentQueue<int> _completionOrder = new ConcurrentQueue<int>();

            public IReadOnlyCollection<int> CompletionOrder => _completionOrder;

            public void RecordCompleted(int sequence)
                => _completionOrder.Enqueue(sequence);

            // Bounded poll until at least minCount messages have completed, returning the observed count (which
            // may be below minCount on timeout — the caller asserts on it so a stalled receive fails fast).
            public async Task<int> WaitForCompletedAsync(int minCount, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (_completionOrder.Count >= minCount)
                    {
                        return _completionOrder.Count;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }

                return _completionOrder.Count;
            }
        }

        // The FIFO fact's handler: the FIRST message sleeps before recording its completion, every other message
        // records immediately. Under the per-session one-message-at-a-time guarantee nothing else can run during
        // that sleep, so completion order equals send order; a second message pulled from the busy session would
        // record ahead of the head and the assertion would fail.
        private sealed class SlowHeadSessionHandler : IMessageHandler<SessionOrderedCommand>
        {
            private readonly SessionCompletionObserver _observer;

            public SlowHeadSessionHandler(SessionCompletionObserver observer)
                => _observer = observer;

            public async Task Handle(SessionOrderedCommand message, IMessageHandlerContext context)
            {
                if (message.Sequence == SlowHeadSequence)
                {
                    await Task.Delay(SlowHeadDelay).ConfigureAwait(false);
                }

                _observer.RecordCompleted(message.Sequence);
            }
        }
    }
}
