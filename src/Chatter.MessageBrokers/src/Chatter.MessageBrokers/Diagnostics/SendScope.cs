using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Chatter.MessageBrokers.Diagnostics
{
    /// <summary>
    /// ONE dispatch call to broker infrastructure, observed end to end: the ADR-0010 off-guard, the start timestamp,
    /// the send span, the trace context that rides out on the message, the failure, and the duration and message
    /// count recorded when the call finishes.
    /// </summary>
    /// <remarks>
    /// WHY THIS TYPE EXISTS. The off-guard discipline is subtle — the guard has to be the FIRST statement, no
    /// timestamp may be read before it passes, <see cref="Activity.Current"/> may be read only INSIDE it and never
    /// AS it, a sampled-out span still has to propagate, and the ambient activity has to be handed back after the
    /// span stops. Getting any of that wrong breaks an application that NEVER OPTED IN, which is the one outcome
    /// ADR-0010 R1–R4 exist to prevent. Every send site that hand-rolls the ceremony is another place to get it
    /// wrong, so the ceremony lives here EXACTLY ONCE and a send site spells out only what is specific to it.
    /// INVARIANT: <c>default(SendScope)</c> is the well-formed OFF value. Every member is callable on it and every
    /// member does nothing, so a call site never tests for off before calling — which is what keeps the off path
    /// from growing branches that could diverge from the on path (ADR-0010 R4). Being a <c>readonly struct</c>, the
    /// off path also allocates nothing at all: <see cref="Open(string, string, string, int)"/> returns before it
    /// builds the observation the on path needs.
    /// INVARIANT: the ambient <see cref="Activity"/> is restored when the scope closes. A send that must become a
    /// FRESH ROOT is started with <see cref="Activity.Current"/> CLEARED, because <see cref="Activity.Start"/>
    /// otherwise adopts the ambient activity as parent (ADR-0010 D6); <see cref="Activity.Stop"/> then restores
    /// what was current when the span started, which is that deliberate <c>null</c> — so without this restore the
    /// host's own ambient activity is DELETED for the remainder of its async flow by a library it merely opted into
    /// observing. The restore is applied on every branch rather than only on that one, so the guarantee a call site
    /// gets — the scope hands back the activity it opened over — does not depend on which parenting branch ran or
    /// on how the base class library happens to restore <see cref="Activity.Current"/> today. This is the same
    /// obligation <c>BrokerDiagnostics.ReceiveSpan</c> discharges on the receive side, and it is discharged here so
    /// that "a send site forgot to restore the ambient" is not a representable defect.
    /// </remarks>
    public readonly struct SendScope : IDisposable
    {
        // The whole on-path state, in ONE reference so the struct stays two fields wide and `default` stays free.
        // Null IS the off-guard for every member below: it is null exactly when Open returned before the guard
        // passed, so no member can emit while an application has not opted in (ADR-0010 R1).
        private readonly SendObservation _observation;
        private readonly Activity _ambientToRestore;

        private SendScope(SendObservation observation, Activity ambientToRestore)
        {
            _observation = observation;
            _ambientToRestore = ambientToRestore;
        }

        /// <summary>
        /// The send span, or <c>null</c> when broker diagnostics are off, an application opted into METRICS ONLY, or
        /// the .NET <c>ActivityListener</c> declined to sample.
        /// </summary>
        public Activity Activity => _observation?.Activity;

        /// <summary>
        /// The <see cref="Activity"/> whose trace context travels with the messages this call carries, or <c>null</c>
        /// when nothing may go onto the wire.
        /// </summary>
        /// <remarks>
        /// Normally the send span itself. When the span was SAMPLED OUT while Chatter .NET
        /// <c>ActivityListener</c>s are still attached, an immediate send falls back to the ambient activity so the
        /// trace does not break at this hop for a downstream hop that samples independently (ADR-0010 D9). A
        /// DEFERRED send — one opened with an explicit parent — has NO such fallback: its causal parent is elsewhere
        /// by definition, so the ambient at drain time is the drain loop, and overwriting the persisted
        /// <c>traceparent</c> the message already carries with it would report a causality that never happened
        /// (ADR-0010 D6). Writing nothing leaves that persisted record on the wire, which is what the downstream hop
        /// must see.
        /// This is exposed rather than kept private because a dispatch whose messages are built by a
        /// <c>yield return</c> iterator injects PER MESSAGE, at enumeration time, from inside the router — so the
        /// activity has to be passed down explicitly rather than injected here (ADR-0010 R2).
        /// </remarks>
        public Activity TraceContextActivity => _observation?.TraceContextActivity;

        /// <summary>
        /// Opens the scope for one dispatch call, parented to whatever <see cref="Activity"/> is current — the
        /// IMMEDIATE send, whose caller genuinely is its parent.
        /// </summary>
        /// <param name="messagingSystem">The value for <see cref="BrokerDiagnostics.MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="BrokerDiagnostics.OperationName"/>; also the first word of the span name.</param>
        /// <param name="destinationName">The value for <see cref="BrokerDiagnostics.DestinationName"/>, or <c>null</c> when the call cannot know it yet.</param>
        /// <param name="messageCount">The value for <see cref="BrokerDiagnostics.BatchMessageCount"/>, or zero when the call cannot know it yet.</param>
        /// <returns>The open scope, or <c>default</c> when the application has not opted into broker diagnostics.</returns>
        public static SendScope Open(string messagingSystem, string operationName, string destinationName, int messageCount)
        {
            if (!BrokerDiagnostics.IsEnabled)
            {
                return default;
            }

            var startTimestamp = Stopwatch.GetTimestamp();
            var ambient = Activity.Current;
            var activity = BrokerDiagnostics.StartSend(messagingSystem, operationName, destinationName, messageCount);

            return Open(startTimestamp, ambient, activity, ResolveTraceContextActivity(activity), messagingSystem, operationName, destinationName, messageCount);
        }

        /// <summary>
        /// Opens the scope for one dispatch call, parented to an EXPLICITLY SUPPLIED trace context — the DEFERRED
        /// send, whose causal parent no longer exists as a running <see cref="Activity"/> and survives only as the
        /// trace context its writer persisted.
        /// </summary>
        /// <param name="messagingSystem">The value for <see cref="BrokerDiagnostics.MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="BrokerDiagnostics.OperationName"/>; also the first word of the span name.</param>
        /// <param name="destinationName">The value for <see cref="BrokerDiagnostics.DestinationName"/>, or <c>null</c> when the call cannot know it yet.</param>
        /// <param name="messageCount">The value for <see cref="BrokerDiagnostics.BatchMessageCount"/>, or zero when the call cannot know it yet.</param>
        /// <param name="parent">The trace context the span is parented to, or <c>default</c> when the caller found none. <c>default</c> means ABSENCE, never "use the current activity".</param>
        /// <returns>The open scope, or <c>default</c> when the application has not opted into broker diagnostics.</returns>
        public static SendScope Open(string messagingSystem, string operationName, string destinationName, int messageCount, ActivityContext parent)
        {
            if (!BrokerDiagnostics.IsEnabled)
            {
                return default;
            }

            var startTimestamp = Stopwatch.GetTimestamp();
            var ambient = Activity.Current;
            var activity = BrokerDiagnostics.StartSend(messagingSystem, operationName, destinationName, messageCount, parent);

            // No sampled-out fallback on this overload, deliberately: see TraceContextActivity.
            return Open(startTimestamp, ambient, activity, activity, messagingSystem, operationName, destinationName, messageCount);
        }

        /// <summary>
        /// Writes the trace context that travels with this call onto <paramref name="messageContext"/>. Writes
        /// nothing when there is no context to travel.
        /// </summary>
        /// <param name="messageContext">The outbound message's context dictionary.</param>
        public void Inject(IDictionary<string, object> messageContext)
        {
            if (_observation is null)
            {
                return;
            }

            TraceContextPropagator.Inject(_observation.TraceContextActivity, messageContext);
        }

        /// <summary>
        /// Marks the call as failed, ONCE, for both the span's status and the metric's <c>error.type</c>, so the two
        /// cannot spell one failure two ways.
        /// </summary>
        /// <param name="exception">The exception that ended the call.</param>
        public void RecordFailure(Exception exception)
        {
            if (_observation is null || exception is null)
            {
                return;
            }

            _observation.Failure = exception;
            BrokerDiagnostics.RecordFailure(_observation.Activity, exception);
        }

        /// <summary>
        /// Reports the destination the call turned out to target, for a call that could not know it at
        /// <c>Open</c> — an attribute-routed dispatch resolves one per message, BY the enumeration the router
        /// performs (ADR-0010 D7).
        /// </summary>
        /// <param name="destinationName">The resolved destination, or <c>null</c> when the call had no single one.</param>
        /// <remarks>REPLACES the destination <c>Open</c> was given, on the span and on the metric alike, so the two
        /// cannot report different destinations for one call. A <c>null</c> or blank value leaves the span attribute
        /// as it stands — an unset attribute is the honest report of "this call had no ONE destination" — while the
        /// metric carries the same blank value, exactly as a call opened without a destination does.</remarks>
        public void RecordResolvedDestination(string destinationName)
        {
            if (_observation is null)
            {
                return;
            }

            _observation.DestinationName = destinationName;
            BrokerDiagnostics.RecordResolvedDestination(_observation.Activity, _observation.OperationName, destinationName);
        }

        /// <summary>
        /// Reports how many messages the call turned out to carry, for a call that could not know it at
        /// <c>Open</c> — a lazily-built batch is counted BY the one enumeration the router performs, never by a
        /// walk of the caller's own sequence (ADR-0010 D7).
        /// </summary>
        /// <param name="messageCount">The messages the call actually handed to broker infrastructure.</param>
        /// <remarks>REPLACES the count <c>Open</c> was given, on the span and on the metric alike. Neither value is
        /// observable to a .NET <c>ActivityListener</c> before the span stops, so rewriting the tag here changes
        /// nothing a listener already saw.</remarks>
        public void RecordResolvedMessageCount(int messageCount)
        {
            if (_observation is null)
            {
                return;
            }

            _observation.MessageCount = messageCount;
            _observation.Activity?.SetTag(BrokerDiagnostics.BatchMessageCount, messageCount);
        }

        /// <summary>
        /// Closes the call: records its duration and message count, stops the span, then restores the ambient
        /// <see cref="Activity"/> the scope opened over.
        /// </summary>
        /// <remarks>
        /// The order is load-bearing. The metric is recorded before the span stops, matching where a hand-rolled
        /// <c>finally</c> sat inside its <c>using</c>. The ambient restore comes AFTER the stop, because stopping
        /// the span is itself what writes <see cref="Activity.Current"/> — to the deliberate <c>null</c> a fresh
        /// root was started over — so restoring first would simply be undone.
        /// INVARIANT: the call is discharged exactly once. A <c>readonly struct</c> is freely copyable, so the
        /// number of copies — and the number of THREADS holding them — is not something a call site can be asked to
        /// control. <see cref="SendObservation.TryClose"/> therefore settles the whole question in ONE
        /// <see cref="Interlocked.Exchange(ref int, int)"/> on the single heap cell every copy shares: exactly one
        /// caller can ever observe the transition, so "closed exactly once" holds BY CONSTRUCTION rather than by a
        /// check-then-set that two concurrent copies could both pass and double-count the send with.
        /// The off-guard stays FIRST and the <c>||</c> short-circuits, so <c>default(SendScope)</c> still returns
        /// without touching the atomic (ADR-0010 R1, R4).
        /// NOT EXCEPTION-SAFE: if <see cref="BrokerDiagnostics.RecordSend"/> or <c>Activity.Dispose()</c> below
        /// throws, the span is left unstopped and the ambient <see cref="Activity"/> is left unrestored. This
        /// hand-writes the same ordered close ceremony as <c>BrokerDiagnostics.ReceiveSpan.Dispose</c>, and shares
        /// its root cause with that sibling: the ceremony lives at each scope type's call site instead of behind
        /// one seam both already cross. The fix is that shared seam, not a <c>try/finally</c> wrapped around this
        /// method alone — that would leave the sibling's copy of the same ceremony untouched, the same lesson
        /// ADR-0010 D11 records for the receive error ladder. Deferred: opt-in diagnostics only, correct on the
        /// normal path, and no worse than what already ships.
        /// </remarks>
        public void Dispose()
        {
            if (_observation is null || !_observation.TryClose())
            {
                return;
            }

            BrokerDiagnostics.RecordSend(
                _observation.StartTimestamp,
                _observation.MessagingSystem,
                _observation.OperationName,
                _observation.DestinationName,
                _observation.MessageCount,
                _observation.Failure);

            _observation.Activity?.Dispose();

            if (_ambientToRestore != null)
            {
                Activity.Current = _ambientToRestore;
            }
        }

        /// <summary>
        /// The sampled-out propagation fallback of the IMMEDIATE send (ADR-0010 D9). Reading
        /// <see cref="Activity.Current"/> is legitimate here only because the caller already ran the off-guard
        /// (ADR-0010 R3); it is never the guard itself (R2).
        /// </summary>
        private static Activity ResolveTraceContextActivity(Activity activity)
            => activity ?? (BrokerDiagnostics.Source.HasListeners() ? Activity.Current : null);

        /// <summary>
        /// Builds the open scope, recording the ambient activity for restoration ONLY when a span was actually
        /// started — nothing else moves <see cref="Activity.Current"/>, so there is otherwise nothing to restore.
        /// </summary>
        private static SendScope Open(long startTimestamp, Activity ambient, Activity activity, Activity traceContextActivity, string messagingSystem, string operationName, string destinationName, int messageCount)
        {
            var observation = new SendObservation
            {
                StartTimestamp = startTimestamp,
                MessagingSystem = messagingSystem,
                OperationName = operationName,
                DestinationName = destinationName,
                MessageCount = messageCount,
                Activity = activity,
                TraceContextActivity = traceContextActivity
            };

            return new SendScope(observation, activity is null ? null : ambient);
        }

        /// <summary>
        /// Everything one dispatch call reports, in one place, so the values a call learns only at STOP time — the
        /// destination, the count and the failure — reach the span and the metric from the SAME record.
        /// </summary>
        private sealed class SendObservation
        {
            /// <summary>The <see cref="Stopwatch.GetTimestamp"/> value read when the call began.</summary>
            internal long StartTimestamp { get; set; }

            internal string MessagingSystem { get; set; }

            internal string OperationName { get; set; }

            /// <summary>The destination reported at stop; replaced by <see cref="RecordResolvedDestination"/>.</summary>
            internal string DestinationName { get; set; }

            /// <summary>The message count reported at stop; replaced by <see cref="RecordResolvedMessageCount"/>.</summary>
            internal int MessageCount { get; set; }

            internal Activity Activity { get; set; }

            internal Activity TraceContextActivity { get; set; }

            /// <summary>The exception that ended the call, or <c>null</c> when it succeeded.</summary>
            internal Exception Failure { get; set; }

            /// <summary>Zero while the call is open, one once it is closed. A FIELD, not a property, because the
            /// close has to be an interlocked operation on the cell itself.</summary>
            private int _closed;

            /// <summary>
            /// Claims the close for the caller, returning <c>true</c> to exactly one caller ever.
            /// </summary>
            /// <remarks>One unconditional atomic swap: the PRIOR value is what decides the winner, so the check and
            /// the set cannot be separated by another thread. Every copy of the owning scope reaches this same
            /// instance, which is what makes copy count irrelevant to the guarantee.</remarks>
            internal bool TryClose() => Interlocked.Exchange(ref _closed, 1) == 0;
        }
    }
}
