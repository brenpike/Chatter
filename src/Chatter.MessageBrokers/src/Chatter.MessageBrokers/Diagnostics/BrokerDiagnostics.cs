using Chatter.CQRS.Diagnostics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Chatter.MessageBrokers.Diagnostics
{
    /// <summary>
    /// The opt-in tracing and metrics surface for brokered messaging. Built on the .NET base class library only
    /// (<see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>), so a
    /// consuming application chooses its own collector without Chatter taking a telemetry dependency.
    /// </summary>
    /// <remarks>
    /// The scope is named for this assembly rather than a flat <c>"Chatter"</c> (ADR-0010 D3): an application opts
    /// in with <c>.AddSource("Chatter.*")</c> / <c>.AddMeter("Chatter.*")</c>, or by naming each scope exactly.
    /// INVARIANT: the off-guard is Chatter's own <see cref="ActivitySource.HasListeners"/> or
    /// <see cref="Instrument.Enabled"/> — never <see cref="Activity.Current"/>, which is non-null in any host that
    /// runs unrelated instrumentation and therefore does not mean Chatter diagnostics are on (ADR-0010 R1, R2).
    /// INVARIANT: every entry point below evaluates its off-guard as its FIRST statement; no span name, tag list or
    /// timestamp is read before that guard passes, so an application that never opted in pays one boolean read.
    /// Span failure is marked through <see cref="ActivityOutcome"/> and the metric's <c>error.type</c> is resolved
    /// through the same type, so a span's status and its metric's error class cannot diverge.
    /// The broker-boundary attribute names are OpenTelemetry messaging semantic conventions pinned to specification
    /// v1.30.0 (https://github.com/open-telemetry/semantic-conventions/blob/v1.30.0/docs/messaging/messaging-spans.md);
    /// names prefixed <c>chatter.</c> are Chatter-native because no convention covers them.
    /// </remarks>
    public static class BrokerDiagnostics
    {
        /// <summary>The name of the <see cref="ActivitySource"/> an application subscribes to for broker spans.</summary>
        public const string ActivitySourceName = "Chatter.MessageBrokers";

        /// <summary>The name of the <see cref="Meter"/> an application subscribes to for broker instruments.</summary>
        public const string MeterName = "Chatter.MessageBrokers";

        /// <summary>The duration of a broker client operation, recorded in seconds. Semconv v1.30.0.</summary>
        public const string OperationDurationInstrumentName = "messaging.client.operation.duration";

        /// <summary>The number of messages handed to broker infrastructure for delivery. Semconv v1.30.0.</summary>
        public const string SentMessagesInstrumentName = "messaging.client.sent.messages";

        /// <summary>
        /// The number of messages delivered by broker infrastructure. Semconv v1.30.0; "consumed" is the pinned
        /// specification's wire spelling for what this context calls receiving, and is telemetry data rather than
        /// domain language.
        /// </summary>
        public const string ConsumedMessagesInstrumentName = "messaging.client.consumed.messages";

        /// <summary>The messaging system the operation ran against. Semconv v1.30.0 <c>messaging.system</c>, Required.</summary>
        public const string MessagingSystem = "messaging.system";

        /// <summary>The broker operation the span describes. Semconv v1.30.0 <c>messaging.operation.name</c>, Required.</summary>
        public const string OperationName = "messaging.operation.name";

        /// <summary>The class of broker operation; values come from <see cref="OperationTypes"/>. Semconv v1.30.0 <c>messaging.operation.type</c>.</summary>
        public const string OperationType = "messaging.operation.type";

        /// <summary>The destination path the operation targeted. Semconv v1.30.0 <c>messaging.destination.name</c>.</summary>
        public const string DestinationName = "messaging.destination.name";

        /// <summary>The broker's identifier for a single message. Semconv v1.30.0 <c>messaging.message.id</c>.</summary>
        public const string MessageId = "messaging.message.id";

        /// <summary>The number of messages in a batch operation. Semconv v1.30.0 <c>messaging.batch.message_count</c>.</summary>
        public const string BatchMessageCount = "messaging.batch.message_count";

        /// <summary>
        /// How many times Recovery has attempted one delivery. Chatter-native: Recovery wraps dispatch, so one
        /// receive span covers every attempt for a delivery and the attempt count is a tag on it (ADR-0010 D7).
        /// </summary>
        public const string ReceiveAttempts = "chatter.messaging.receive.attempts";

        /// <summary>
        /// How a delivery was settled; values come from <see cref="Settlements"/>. Chatter-native: semconv v1.30.0
        /// defines a <c>settle</c> operation type but no broker-neutral settlement-outcome attribute.
        /// </summary>
        public const string Settlement = "chatter.messaging.settlement";

        /// <summary>
        /// The name of the span event carrying one Recovery retry of a single delivery. Chatter-native: semconv
        /// v1.30.0 defines no retry event, and its <c>settle</c> operation type describes a settlement rather than a
        /// re-attempt.
        /// </summary>
        public const string ReceiveRetryEventName = "chatter.messaging.receive.retry";

        private static readonly string _telemetryVersion = ResolveTelemetryVersion();
        private static readonly ActivitySource _source = new ActivitySource(ActivitySourceName, _telemetryVersion);
        private static readonly Meter _meter = new Meter(MeterName, _telemetryVersion);
        private static readonly Histogram<double> _operationDuration = _meter.CreateHistogram<double>(OperationDurationInstrumentName, "s", "Duration of a Chatter broker client operation.");
        private static readonly Counter<long> _sentMessages = _meter.CreateCounter<long>(SentMessagesInstrumentName, "{message}", "Number of messages Chatter handed to broker infrastructure for delivery.");
        private static readonly Counter<long> _consumedMessages = _meter.CreateCounter<long>(ConsumedMessagesInstrumentName, "{message}", "Number of messages broker infrastructure delivered to Chatter.");

        /// <summary>
        /// The <see cref="ActivitySource"/> broker spans are emitted from. Exposed so a call site can run the
        /// <see cref="ActivitySource.HasListeners"/> off-guard itself before building any argument.
        /// </summary>
        /// <remarks>
        /// Internal, not public: an application opts in by NAME through <c>.AddSource("Chatter.*")</c> /
        /// <c>.AddMeter("Chatter.*")</c> (ADR-0010 D3), so <see cref="ActivitySourceName"/> is the contract and the
        /// instance itself never was. The one need the instance served — a call site running the off-guard before
        /// building an argument — is entirely in-assembly. Widening this later is non-breaking; narrowing it after
        /// the package ships is not.
        /// </remarks>
        internal static ActivitySource Source => _source;

        /// <summary>
        /// Whether an application has opted into broker diagnostics, either by attaching a .NET
        /// <c>ActivityListener</c> to the <see cref="ActivitySourceName"/> scope or by enabling one of the
        /// <see cref="MeterName"/> scope's instruments on a .NET <c>MeterListener</c>. This is the outer guard a
        /// call site checks before reading a start timestamp; it is an OR across tracing AND metrics, so enabling
        /// only an instrument is enough to take the instrumented path with no .NET <c>ActivityListener</c> attached.
        /// </summary>
        public static bool IsEnabled => _source.HasListeners() || _operationDuration.Enabled || _sentMessages.Enabled || _consumedMessages.Enabled;

        /// <summary>
        /// Starts a span covering one dispatch call to broker infrastructure, or returns <c>null</c> when no .NET
        /// <c>ActivityListener</c> is attached to the <see cref="ActivitySourceName"/> scope or the listener
        /// declined to sample.
        /// </summary>
        /// <param name="messagingSystem">The value for <see cref="MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="OperationName"/>; also the first word of the span name.</param>
        /// <param name="destinationName">The value for <see cref="DestinationName"/>; also the second word of the span name.</param>
        /// <param name="messageCount">The value for <see cref="BatchMessageCount"/>: one dispatch call carries N messages that share one context (ADR-0010 D7). A caller that cannot know N until its batch has been enumerated passes zero here and rewrites the tag before the span stops.</param>
        /// <returns>The started <see cref="Activity"/>, or <c>null</c>.</returns>
        public static Activity StartSend(string messagingSystem, string operationName, string destinationName, int messageCount)
        {
            if (!_source.HasListeners())
            {
                return null;
            }

            var activity = _source.StartActivity(BuildSpanName(operationName, destinationName), ActivityKind.Producer);

            if (activity is null)
            {
                return null;
            }

            SetOperationTags(activity, messagingSystem, operationName, OperationTypes.Send, destinationName);
            activity.SetTag(BatchMessageCount, messageCount);
            return activity;
        }

        /// <summary>
        /// Starts a span covering one delivery from broker infrastructure, parented to the trace context the
        /// producer wrote onto <paramref name="messageContext"/>, or returns <c>null</c> when no .NET
        /// <c>ActivityListener</c> is attached to the <see cref="ActivitySourceName"/> scope or the listener
        /// declined to sample.
        /// </summary>
        /// <param name="messagingSystem">The value for <see cref="MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="OperationName"/>; also the first word of the span name.</param>
        /// <param name="destinationName">The value for <see cref="DestinationName"/>; also the second word of the span name.</param>
        /// <param name="messageId">The value for <see cref="MessageId"/>, or <c>null</c> when the infrastructure supplied none.</param>
        /// <param name="messageContext">The inbound message's context dictionary, carrying the producer's trace context.</param>
        /// <returns>
        /// The <see cref="ReceiveSpan"/> scope for the delivery. Its <see cref="ReceiveSpan.Activity"/> is <c>null</c>
        /// when no span was started, and disposing the scope both stops the span and restores any ambient activity the
        /// start suppressed.
        /// </returns>
        /// <remarks>The extracted context is the PARENT and an ambient activity that differs from it is attached as a
        /// LINK, never promoted to parent: a message's causal parent is its producer, and re-parenting to whatever
        /// happened to be current at delivery time would sever the distributed trace at every hop (ADR-0010 D6).</remarks>
        public static ReceiveSpan StartReceive(string messagingSystem, string operationName, string destinationName, string messageId, IReadOnlyDictionary<string, object> messageContext)
        {
            if (!_source.HasListeners())
            {
                return default;
            }

            var spanName = BuildSpanName(operationName, destinationName);
            Activity activity;
            Activity suppressedAmbient;

            if (TraceContextPropagator.TryExtract(messageContext, out var producerContext))
            {
                activity = _source.StartActivity(spanName, ActivityKind.Consumer, producerContext, links: BuildAmbientLinks(producerContext));
                suppressedAmbient = null;
            }
            else
            {
                activity = StartHeaderlessReceive(spanName, out suppressedAmbient);
            }

            if (activity is null)
            {
                return default;
            }

            SetOperationTags(activity, messagingSystem, operationName, OperationTypes.Receive, destinationName);

            if (!string.IsNullOrEmpty(messageId))
            {
                activity.SetTag(MessageId, messageId);
            }

            return new ReceiveSpan(activity, suppressedAmbient);
        }

        /// <summary>
        /// Marks <paramref name="activity"/> as failed, through <see cref="ActivityOutcome"/> so a broker span and a
        /// dispatch span record failure identically.
        /// </summary>
        /// <param name="activity">The span to mark, or <c>null</c> when no span was started.</param>
        /// <param name="exception">The exception that ended the operation.</param>
        public static void RecordFailure(Activity activity, Exception exception)
        {
            if (!_source.HasListeners())
            {
                return;
            }

            ActivityOutcome.RecordFailure(activity, exception);
        }

        /// <summary>
        /// Marks <paramref name="activity"/> as failed when NO exception was raised, through
        /// <see cref="ActivityOutcome"/> so a broker span records an exception-shaped failure and a
        /// description-shaped one identically.
        /// </summary>
        /// <param name="activity">The span to mark, or <c>null</c> when no span was started.</param>
        /// <param name="errorType">The class of failure; values come from <see cref="ErrorTypes"/>.</param>
        /// <param name="description">What did not happen, carried as the span's status description.</param>
        /// <remarks>No <c>exception</c> span event is added, because no exception exists: a never-thrown marker
        /// exception would stamp a synthetic stack trace describing nothing that happened.</remarks>
        public static void RecordFailure(Activity activity, string errorType, string description)
        {
            if (!_source.HasListeners())
            {
                return;
            }

            ActivityOutcome.RecordFailure(activity, errorType, description);
        }

        /// <summary>
        /// Records how a delivery was settled on <paramref name="activity"/>.
        /// </summary>
        /// <param name="activity">The receive span, or <c>null</c> when no span was started.</param>
        /// <param name="settlement">One of <see cref="Settlements"/>.</param>
        public static void RecordSettlement(Activity activity, string settlement)
        {
            if (!_source.HasListeners())
            {
                return;
            }

            activity?.SetTag(Settlement, settlement);
        }

        /// <summary>
        /// Records the duration and message count of one dispatch call to broker infrastructure.
        /// </summary>
        /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> value read when the operation began.</param>
        /// <param name="messagingSystem">The value for <see cref="MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="OperationName"/>.</param>
        /// <param name="destinationName">The value for <see cref="DestinationName"/>.</param>
        /// <param name="messageCount">How many messages the dispatch call carried.</param>
        /// <param name="exception">The exception that ended the operation, or <c>null</c> when it succeeded.</param>
        public static void RecordSend(long startTimestamp, string messagingSystem, string operationName, string destinationName, int messageCount, Exception exception)
        {
            if (!_operationDuration.Enabled && !_sentMessages.Enabled)
            {
                return;
            }

            var tags = BuildOperationTags(messagingSystem, operationName, OperationTypes.Send, destinationName, ActivityOutcome.ResolveErrorType(exception));

            if (_operationDuration.Enabled)
            {
                _operationDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
            }

            if (_sentMessages.Enabled)
            {
                _sentMessages.Add(messageCount, tags);
            }
        }

        /// <summary>
        /// Records the duration of one delivery from broker infrastructure, and counts the delivered message.
        /// </summary>
        /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> value read when the delivery began.</param>
        /// <param name="messagingSystem">The value for <see cref="MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="OperationName"/>.</param>
        /// <param name="destinationName">The value for <see cref="DestinationName"/>.</param>
        /// <param name="exception">The exception that ended the delivery, or <c>null</c> when it succeeded.</param>
        /// <remarks>INVARIANT: the off-guard is re-evaluated here rather than left to the overload this delegates to,
        /// so the error type is resolved only once an instrument is enabled and the off path stays a boolean read
        /// (ADR-0010 R1).</remarks>
        public static void RecordReceive(long startTimestamp, string messagingSystem, string operationName, string destinationName, Exception exception)
        {
            if (!_operationDuration.Enabled && !_consumedMessages.Enabled)
            {
                return;
            }

            RecordReceive(startTimestamp, messagingSystem, operationName, destinationName, ActivityOutcome.ResolveErrorType(exception));
        }

        /// <summary>
        /// Records the duration of one delivery from broker infrastructure, and counts the delivered message, for a
        /// delivery whose failure is not carried by an exception.
        /// </summary>
        /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> value read when the delivery began.</param>
        /// <param name="messagingSystem">The value for <see cref="MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="OperationName"/>.</param>
        /// <param name="destinationName">The value for <see cref="DestinationName"/>.</param>
        /// <param name="errorType">The class of failure that ended the delivery, or <c>null</c> when it succeeded; values come from <see cref="ErrorTypes"/>.</param>
        public static void RecordReceive(long startTimestamp, string messagingSystem, string operationName, string destinationName, string errorType)
        {
            if (!_operationDuration.Enabled && !_consumedMessages.Enabled)
            {
                return;
            }

            var tags = BuildOperationTags(messagingSystem, operationName, OperationTypes.Receive, destinationName, errorType);

            if (_operationDuration.Enabled)
            {
                _operationDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
            }

            if (_consumedMessages.Enabled)
            {
                _consumedMessages.Add(1, tags);
            }
        }

        /// <summary>
        /// Writes the destination a dispatch call turned out to target onto <paramref name="activity"/>, for the
        /// overloads whose destination is not known until the messages have been routed.
        /// </summary>
        /// <param name="activity">The send span, or <c>null</c> when no span was started.</param>
        /// <param name="operationName">The value of <see cref="OperationName"/>; the first word of the span name.</param>
        /// <param name="destinationName">The resolved destination, or <c>null</c> to leave the attribute unset.</param>
        /// <remarks>
        /// Called at span STOP rather than at start, because the destination of an attribute-routed dispatch is
        /// resolved BY the one enumeration the Router performs and is therefore unknown when the span begins — exactly
        /// as <see cref="BatchMessageCount"/> is (ADR-0010 D7). Neither value is observable to a .NET
        /// <c>ActivityListener</c> before the span stops, so rewriting the span name here changes nothing a listener
        /// already saw: <see cref="ActivitySource.StartActivity"/> has made its sampling decision by then, and
        /// <see cref="Activity.DisplayName"/> is read at <c>ActivityStopped</c>.
        /// </remarks>
        public static void RecordResolvedDestination(Activity activity, string operationName, string destinationName)
        {
            if (!_source.HasListeners() || activity is null || string.IsNullOrEmpty(destinationName))
            {
                return;
            }

            activity.SetTag(DestinationName, destinationName);
            activity.DisplayName = BuildSpanName(operationName, destinationName);
        }

        /// <summary>
        /// The span name convention of semconv v1.30.0: <c>{messaging.operation.name} {destination}</c>.
        /// </summary>
        private static string BuildSpanName(string operationName, string destinationName)
            => string.IsNullOrEmpty(destinationName) ? operationName : operationName + " " + destinationName;

        private static void SetOperationTags(Activity activity, string messagingSystem, string operationName, string operationType, string destinationName)
        {
            activity.SetTag(MessagingSystem, messagingSystem);
            activity.SetTag(OperationName, operationName);
            activity.SetTag(OperationType, operationType);
            activity.SetTag(DestinationName, destinationName);
        }

        /// <summary>
        /// Builds the metric attribute set from an ALREADY RESOLVED <paramref name="errorType"/>. <see cref="TagList"/>
        /// is a struct that holds up to eight entries inline, so no array is allocated, and it is only ever built
        /// inside an <see cref="Instrument.Enabled"/> guard.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the error type is resolved by the CALLER, so the exception-shaped and description-shaped
        /// failure paths meet at one point and cannot spell one failure two ways. The emptiness test mirrors
        /// <see cref="ActivityOutcome.RecordFailure(Activity, string, string)"/>'s own no-op condition, so a span's
        /// status and its metric's <c>error.type</c> cannot diverge on a blank error type either.
        /// </remarks>
        private static TagList BuildOperationTags(string messagingSystem, string operationName, string operationType, string destinationName, string errorType)
        {
            var tags = new TagList
            {
                { MessagingSystem, messagingSystem },
                { OperationName, operationName },
                { OperationType, operationType },
                { DestinationName, destinationName }
            };

            if (!string.IsNullOrWhiteSpace(errorType))
            {
                tags.Add(ChatterTelemetryTags.ErrorType, errorType);
            }

            return tags;
        }

        /// <summary>
        /// Starts the receive span for a delivery that carried no usable trace context, as a FRESH ROOT with the
        /// ambient activity attached as a LINK rather than promoted to parent (ADR-0010 D6).
        /// </summary>
        /// <remarks>
        /// WHY THE AMBIENT ACTIVITY IS SUPPRESSED RATHER THAN JUST NOT PASSED. Neither
        /// <c>StartActivity(name, kind)</c> nor <c>StartActivity(name, kind, default(ActivityContext))</c> produces a
        /// root: <see cref="Activity.Start"/> falls back to <see cref="Activity.Current"/> whenever the created
        /// activity was given neither a parent id nor a parent span id, and a default <see cref="ActivityContext"/>
        /// supplies neither. A headerless delivery would therefore become a CHILD of whatever unrelated host
        /// activity happened to be current at delivery time — receiver startup or a poll loop can flow one in
        /// through <c>Task.Run</c> — producing false causality, which is exactly what D6 rejects on the
        /// extracted-parent branch. <see cref="Activity.Current"/> is cleared across the start call so the fallback
        /// cannot fire. The ambient is then restored on BOTH sampling outcomes, because the suppression exists to keep
        /// the span off the ambient's tree, never to end the ambient:
        /// <list type="bullet">
        /// <item><description>SAMPLED OUT — no span exists, so the ambient is restored IMMEDIATELY, before this method
        /// returns, and the sampled-out propagation fallback still sees it (ADR-0010 D9).</description></item>
        /// <item><description>SAMPLED IN — the span must stay current for the whole delivery, so the ambient is handed
        /// back through <paramref name="suppressedAmbient"/> and restored by <see cref="ReceiveSpan.Dispose"/> AFTER the
        /// span stops. <see cref="Activity.Stop"/> restores what the started activity recorded as its parent, which is
        /// null here precisely because the ambient was cleared, so without that restore the ambient would be lost for
        /// the remainder of the delivery's async flow.</description></item>
        /// </list>
        /// Reading and writing <see cref="Activity.Current"/> here is legitimate only because the caller already ran the
        /// <see cref="ActivitySource.HasListeners"/> guard (ADR-0010 R3), so an application that never opted in
        /// never reaches this method.
        /// </remarks>
        /// <param name="spanName">The span name, already built by the caller.</param>
        /// <param name="suppressedAmbient">
        /// The ambient activity whose restoration is still OUTSTANDING when this returns, or <c>null</c> when there is
        /// nothing left to restore — no ambient existed, or the span was sampled out and the ambient is already back.
        /// </param>
        private static Activity StartHeaderlessReceive(string spanName, out Activity suppressedAmbient)
        {
            var ambient = Activity.Current;
            suppressedAmbient = null;

            if (ambient is null)
            {
                return _source.StartActivity(spanName, ActivityKind.Consumer);
            }

            var ambientLinks = new[] { new ActivityLink(ambient.Context) };
            Activity activity = null;

            Activity.Current = null;

            try
            {
                activity = _source.StartActivity(spanName, ActivityKind.Consumer, default(ActivityContext), links: ambientLinks);
            }
            finally
            {
                if (activity is null)
                {
                    Activity.Current = ambient;
                }
                else
                {
                    suppressedAmbient = ambient;
                }
            }

            return activity;
        }

        /// <summary>
        /// The ambient activity as a link, per ADR-0010 D6. Reading <see cref="Activity.Current"/> is legitimate here
        /// only because this runs inside the <see cref="ActivitySource.HasListeners"/> guard (ADR-0010 R3).
        /// </summary>
        private static ActivityLink[] BuildAmbientLinks(ActivityContext producerContext)
        {
            var ambient = Activity.Current;

            if (ambient is null || ambient.Context == producerContext)
            {
                return null;
            }

            return new[] { new ActivityLink(ambient.Context) };
        }

        private static string ResolveTelemetryVersion()
        {
            var assembly = typeof(BrokerDiagnostics).Assembly;
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrEmpty(informationalVersion))
            {
                return assembly.GetName().Version?.ToString();
            }

            var buildMetadataIndex = informationalVersion.IndexOf('+');
            return buildMetadataIndex < 0 ? informationalVersion : informationalVersion.Substring(0, buildMetadataIndex);
        }

        /// <summary>
        /// One delivery's receive span TOGETHER WITH the ambient <see cref="Activity"/> its start suppressed, so
        /// stopping the span and restoring that ambient are ONE disposal rather than two obligations on a call site.
        /// </summary>
        /// <remarks>
        /// WHY A SCOPE RATHER THAN A BARE <see cref="Activity"/>. A headerless delivery must become a fresh ROOT, and
        /// that costs an explicit <see cref="Activity.Current"/> clear across the start call because
        /// <see cref="Activity.Start"/> otherwise falls back to the ambient activity as parent (ADR-0010 D6 as
        /// amended). <see cref="Activity.Stop"/> then restores what the started activity recorded as its PARENT —
        /// null, precisely because the ambient was cleared — so a call site holding only the <see cref="Activity"/>
        /// would leave the ambient permanently lost on that async flow the moment the span stopped. Returning a scope
        /// makes the restore impossible to forget: there is no way to obtain the span WITHOUT the disposal that
        /// restores the ambient, so "a receive-span call site forgot to restore the suppressed ambient" is not a
        /// representable defect rather than one this type merely happens to avoid.
        /// The ambient is restored rather than left cleared because the suppression exists ONLY to keep the receive
        /// span off the ambient's tree (ADR-0010 D6); the caller's ambient activity is not Chatter's to end.
        /// A <c>readonly struct</c>, so an application that never opted in still allocates nothing on this path and
        /// <c>default</c> is the well-formed "no span was started" value (ADR-0010 R1, R4).
        /// </remarks>
        public readonly struct ReceiveSpan : IDisposable
        {
            private readonly Activity _suppressedAmbient;

            internal ReceiveSpan(Activity activity, Activity suppressedAmbient)
            {
                Activity = activity;
                _suppressedAmbient = suppressedAmbient;
            }

            /// <summary>
            /// The receive span, or <c>null</c> when no .NET <c>ActivityListener</c> is attached to the
            /// <see cref="ActivitySourceName"/> scope or the listener declined to sample.
            /// </summary>
            public Activity Activity { get; }

            /// <summary>
            /// Stops the receive span, then restores the ambient activity the start suppressed. The order matters:
            /// stopping the span sets <see cref="Activity.Current"/> to the span's own (null) parent, so the restore
            /// has to come after it.
            /// </summary>
            public void Dispose()
            {
                Activity?.Dispose();

                if (_suppressedAmbient != null)
                {
                    System.Diagnostics.Activity.Current = _suppressedAmbient;
                }
            }
        }

        /// <summary>
        /// The permitted values of the <see cref="OperationType"/> attribute, per semconv v1.30.0.
        /// </summary>
        public static class OperationTypes
        {
            /// <summary>A message was created before being sent.</summary>
            public const string Create = "create";

            /// <summary>One or more messages were handed to broker infrastructure for delivery.</summary>
            public const string Send = "send";

            /// <summary>One or more messages were requested or delivered from broker infrastructure.</summary>
            public const string Receive = "receive";

            /// <summary>A delivered message was relayed to its handler.</summary>
            public const string Process = "process";

            /// <summary>A delivery was settled with broker infrastructure.</summary>
            public const string Settle = "settle";
        }

        /// <summary>
        /// The permitted values of the <see cref="Settlement"/> attribute, one per settlement the receiving surface
        /// offers (<c>AckMessageAsync</c>, <c>NackMessageAsync</c>, <c>DeadletterMessageAsync</c>).
        /// </summary>
        public static class Settlements
        {
            /// <summary>The delivery was acknowledged and removed from its destination.</summary>
            public const string Ack = "ack";

            /// <summary>The delivery was negatively acknowledged and returned for redelivery.</summary>
            public const string Nack = "nack";

            /// <summary>The delivery was moved to the Error Queue.</summary>
            public const string Deadletter = "deadletter";
        }

        /// <summary>
        /// The values Chatter emits for <c>error.type</c> when the failure raised NO exception, so there is no
        /// exception type name to take it from. Semconv v1.30.0 leaves the vocabulary to the instrumentation for
        /// exactly this case; an exception-shaped failure keeps taking the fully qualified type name through
        /// <see cref="ActivityOutcome.ResolveErrorType"/>.
        /// </summary>
        public static class ErrorTypes
        {
            /// <summary>Broker infrastructure was asked to settle a delivery and reported that it did not.</summary>
            public const string SettlementFailed = "settlement_failed";
        }
    }
}
