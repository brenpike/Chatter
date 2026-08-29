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
        public static ActivitySource Source => _source;

        /// <summary>
        /// Whether an application has opted into broker diagnostics, either by attaching a .NET
        /// <c>ActivityListener</c> to <see cref="Source"/> or by enabling one of the instruments on a .NET
        /// <c>MeterListener</c>. This is the outer guard a call site checks before reading a start timestamp.
        /// </summary>
        public static bool IsEnabled => _source.HasListeners() || _operationDuration.Enabled || _sentMessages.Enabled || _consumedMessages.Enabled;

        /// <summary>
        /// Starts a span covering one dispatch call to broker infrastructure, or returns <c>null</c> when no .NET
        /// <c>ActivityListener</c> is attached to <see cref="Source"/> or the listener declined to sample.
        /// </summary>
        /// <param name="messagingSystem">The value for <see cref="MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="OperationName"/>; also the first word of the span name.</param>
        /// <param name="destinationName">The value for <see cref="DestinationName"/>; also the second word of the span name.</param>
        /// <param name="messageCount">The value for <see cref="BatchMessageCount"/>: one dispatch call carries N messages that share one context (ADR-0010 D7).</param>
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
        /// <c>ActivityListener</c> is attached to <see cref="Source"/> or the listener declined to sample.
        /// </summary>
        /// <param name="messagingSystem">The value for <see cref="MessagingSystem"/>.</param>
        /// <param name="operationName">The value for <see cref="OperationName"/>; also the first word of the span name.</param>
        /// <param name="destinationName">The value for <see cref="DestinationName"/>; also the second word of the span name.</param>
        /// <param name="messageId">The value for <see cref="MessageId"/>, or <c>null</c> when the infrastructure supplied none.</param>
        /// <param name="messageContext">The inbound message's context dictionary, carrying the producer's trace context.</param>
        /// <returns>The started <see cref="Activity"/>, or <c>null</c>.</returns>
        /// <remarks>The extracted context is the PARENT and an ambient activity that differs from it is attached as a
        /// LINK, never promoted to parent: a message's causal parent is its producer, and re-parenting to whatever
        /// happened to be current at delivery time would sever the distributed trace at every hop (ADR-0010 D6).</remarks>
        public static Activity StartReceive(string messagingSystem, string operationName, string destinationName, string messageId, IReadOnlyDictionary<string, object> messageContext)
        {
            if (!_source.HasListeners())
            {
                return null;
            }

            var spanName = BuildSpanName(operationName, destinationName);

            var activity = TraceContextPropagator.TryExtract(messageContext, out var producerContext)
                ? _source.StartActivity(spanName, ActivityKind.Consumer, producerContext, links: BuildAmbientLinks(producerContext))
                : _source.StartActivity(spanName, ActivityKind.Consumer);

            if (activity is null)
            {
                return null;
            }

            SetOperationTags(activity, messagingSystem, operationName, OperationTypes.Receive, destinationName);

            if (!string.IsNullOrEmpty(messageId))
            {
                activity.SetTag(MessageId, messageId);
            }

            return activity;
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

            var tags = BuildOperationTags(messagingSystem, operationName, OperationTypes.Send, destinationName, exception);

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
        public static void RecordReceive(long startTimestamp, string messagingSystem, string operationName, string destinationName, Exception exception)
        {
            if (!_operationDuration.Enabled && !_consumedMessages.Enabled)
            {
                return;
            }

            var tags = BuildOperationTags(messagingSystem, operationName, OperationTypes.Receive, destinationName, exception);

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
        /// Builds the metric attribute set. <see cref="TagList"/> is a struct that holds up to eight entries inline,
        /// so no array is allocated, and it is only ever built inside an <see cref="Instrument.Enabled"/> guard.
        /// </summary>
        private static TagList BuildOperationTags(string messagingSystem, string operationName, string operationType, string destinationName, Exception exception)
        {
            var tags = new TagList
            {
                { MessagingSystem, messagingSystem },
                { OperationName, operationName },
                { OperationType, operationType },
                { DestinationName, destinationName }
            };

            if (exception != null)
            {
                tags.Add(ChatterTelemetryTags.ErrorType, ActivityOutcome.ResolveErrorType(exception));
            }

            return tags;
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
    }
}
