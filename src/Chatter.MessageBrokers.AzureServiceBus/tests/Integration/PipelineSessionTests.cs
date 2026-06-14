using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus.Options;
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
    // Concurrency is pinned to 1 (WithMaxConcurrentCalls(1)) so the pump's per-message worker gate cannot
    // interleave handling of two messages from the held session — the FIFO assertion observes the order
    // Chatter handed messages to the handler, not a race in the test's own observation.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineSessionTests
    {
        private const string SessionQueue = "chatter.session";
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(60);

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

        // Builds the SendOptions that route a message to the given session. SendOptions.WithGroupId stamps
        // MessageContext.GroupId, which the existing ASB mapping forwards to ServiceBusMessage.SessionId. The
        // mapping (OutboundBrokeredMessageExtensions.AsAzureServiceBusMessage) assigns SessionId then
        // PartitionKey on the SDK message, and the SDK requires PartitionKey == SessionId for a session/
        // partitioned message — so the matching PartitionKey must be set alongside the Group Id or the setter
        // throws (this is the documented send-side contract, pinned by the unit test
        // MustMapSessionIdFromGroupIdWhenPartitionKeyMatches). Set both to the session id here.
        private static SendOptions SessionSend(string sessionId)
        {
            var options = new SendOptions().WithGroupId(sessionId);
            options.WithMessageContext(ASBMessageContext.PartitionKey, sessionId);
            return options;
        }

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
    }
}
