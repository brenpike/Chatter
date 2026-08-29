using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Chatter.CQRS.Diagnostics
{
    /// <summary>
    /// The opt-in tracing and metrics surface for Chatter dispatch. Built on the .NET base class library only
    /// (<see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>), so
    /// consuming applications choose their own collector without Chatter taking a telemetry dependency.
    /// </summary>
    /// <remarks>
    /// Diagnostics are OFF until an application opts in by attaching a .NET <c>ActivityListener</c> to
    /// <see cref="Source"/> or by enabling the <see cref="DispatchDurationInstrumentName"/> instrument on a
    /// .NET <c>MeterListener</c>. While off, every entry point below returns after a single boolean read and
    /// allocates nothing.
    /// INVARIANT: the off-guard is Chatter's own <see cref="ActivitySource.HasListeners"/> or
    /// <see cref="Instrument.Enabled"/> — never <see cref="Activity.Current"/>, which is non-null in any host
    /// that runs unrelated instrumentation and therefore does not mean Chatter diagnostics are on.
    /// INVARIANT: every public entry point evaluates its off-guard as its first statement, and no span name,
    /// tag collection, timestamp or per-closed-generic static is read before that guard passes.
    /// </remarks>
    public static class ChatterDiagnostics
    {
        /// <summary>The name of the <see cref="ActivitySource"/> an application subscribes to for Chatter spans.</summary>
        public const string ActivitySourceName = "Chatter.CQRS";

        /// <summary>The name of the <see cref="Meter"/> an application subscribes to for Chatter instruments.</summary>
        public const string MeterName = "Chatter.CQRS";

        /// <summary>The name of the dispatch duration histogram, recorded in seconds.</summary>
        public const string DispatchDurationInstrumentName = "chatter.cqrs.dispatch.duration";

        private static readonly string _telemetryVersion = ResolveTelemetryVersion();
        private static readonly ActivitySource _source = new ActivitySource(ActivitySourceName, _telemetryVersion);
        private static readonly Meter _meter = new Meter(MeterName, _telemetryVersion);
        private static readonly Histogram<double> _dispatchDuration = _meter.CreateHistogram<double>(DispatchDurationInstrumentName, "s", "Duration of a Chatter CQRS dispatch.");
        private static readonly ConcurrentDictionary<Type, string> _spanNamesByMessageType = new ConcurrentDictionary<Type, string>();
        private static readonly Func<Type, string> _spanNameFactory = BuildSpanName;

        /// <summary>
        /// The <see cref="ActivitySource"/> Chatter emits dispatch spans from. Exposed so a call site can run the
        /// <see cref="ActivitySource.HasListeners"/> off-guard itself before building any argument.
        /// </summary>
        public static ActivitySource Source => _source;

        /// <summary>
        /// Whether an application has opted into Chatter diagnostics, either by attaching a .NET
        /// <c>ActivityListener</c> to <see cref="Source"/> or by enabling the dispatch duration histogram.
        /// This is the outer guard a dispatch path checks before doing any diagnostics work.
        /// </summary>
        public static bool IsEnabled => _source.HasListeners() || _dispatchDuration.Enabled;

        /// <summary>
        /// Starts a dispatch span for <typeparamref name="TMessage"/>, or returns <c>null</c> when no .NET
        /// <c>ActivityListener</c> is attached to <see cref="Source"/> or the listener declined to sample.
        /// </summary>
        /// <typeparam name="TMessage">The compile-time type of the message being dispatched.</typeparam>
        /// <param name="dispatchKind">One of <see cref="ChatterTelemetryTags.DispatchKinds"/>.</param>
        /// <returns>The started <see cref="Activity"/>, or <c>null</c>.</returns>
        public static Activity StartDispatch<TMessage>(string dispatchKind)
        {
            if (!_source.HasListeners())
            {
                return null;
            }

            // INVARIANT: reading a static of the closed generic DispatchNames<TMessage> costs a runtime generic
            // dictionary lookup from shared generic code, so it must stay inside the guard above.
            return StartDispatchActivity(DispatchNames<TMessage>.SpanName, DispatchNames<TMessage>.MessageTypeName, dispatchKind);
        }

        /// <summary>
        /// Starts a dispatch span for a message whose type is only known at run time, or returns <c>null</c> when
        /// no .NET <c>ActivityListener</c> is attached to <see cref="Source"/> or the listener declined to sample.
        /// </summary>
        /// <param name="messageType">The run-time type of the message being dispatched.</param>
        /// <param name="dispatchKind">One of <see cref="ChatterTelemetryTags.DispatchKinds"/>.</param>
        /// <returns>The started <see cref="Activity"/>, or <c>null</c>.</returns>
        public static Activity StartDispatch(Type messageType, string dispatchKind)
        {
            if (!_source.HasListeners())
            {
                return null;
            }

            if (messageType is null)
            {
                throw new ArgumentNullException(nameof(messageType));
            }

            return StartDispatchActivity(_spanNamesByMessageType.GetOrAdd(messageType, _spanNameFactory), messageType.FullName, dispatchKind);
        }

        /// <summary>
        /// Records the duration of a dispatch of <typeparamref name="TMessage"/>, in seconds.
        /// </summary>
        /// <typeparam name="TMessage">The compile-time type of the message that was dispatched.</typeparam>
        /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> value read when dispatch began.</param>
        /// <param name="dispatchKind">One of <see cref="ChatterTelemetryTags.DispatchKinds"/>.</param>
        /// <param name="errorType">The value for <see cref="ChatterTelemetryTags.ErrorType"/>, or <c>null</c> when the dispatch succeeded.</param>
        public static void RecordDispatchDuration<TMessage>(long startTimestamp, string dispatchKind, string errorType)
        {
            if (!_dispatchDuration.Enabled)
            {
                return;
            }

            RecordDispatchDuration(startTimestamp, DispatchNames<TMessage>.MessageTypeName, dispatchKind, errorType);
        }

        /// <summary>
        /// Records the duration of a dispatch of a message whose type is only known at run time, in seconds.
        /// </summary>
        /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> value read when dispatch began.</param>
        /// <param name="messageType">The run-time type of the message that was dispatched.</param>
        /// <param name="dispatchKind">One of <see cref="ChatterTelemetryTags.DispatchKinds"/>.</param>
        /// <param name="errorType">The value for <see cref="ChatterTelemetryTags.ErrorType"/>, or <c>null</c> when the dispatch succeeded.</param>
        public static void RecordDispatchDuration(long startTimestamp, Type messageType, string dispatchKind, string errorType)
        {
            if (!_dispatchDuration.Enabled)
            {
                return;
            }

            if (messageType is null)
            {
                throw new ArgumentNullException(nameof(messageType));
            }

            RecordDispatchDuration(startTimestamp, messageType.FullName, dispatchKind, errorType);
        }

        private static Activity StartDispatchActivity(string spanName, string messageTypeName, string dispatchKind)
        {
            var activity = _source.StartActivity(spanName, ActivityKind.Internal);

            if (activity is null)
            {
                return null;
            }

            activity.SetTag(ChatterTelemetryTags.MessageType, messageTypeName);
            activity.SetTag(ChatterTelemetryTags.DispatchKind, dispatchKind);
            return activity;
        }

        private static void RecordDispatchDuration(long startTimestamp, string messageTypeName, string dispatchKind, string errorType)
        {
            var tags = new TagList
            {
                { ChatterTelemetryTags.MessageType, messageTypeName },
                { ChatterTelemetryTags.DispatchKind, dispatchKind }
            };

            if (!string.IsNullOrEmpty(errorType))
            {
                tags.Add(ChatterTelemetryTags.ErrorType, errorType);
            }

            _dispatchDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
        }

        private static string BuildSpanName(Type messageType) => "dispatch " + messageType.Name;

        private static string ResolveTelemetryVersion()
        {
            var assembly = typeof(ChatterDiagnostics).Assembly;
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrEmpty(informationalVersion))
            {
                return assembly.GetName().Version?.ToString();
            }

            var buildMetadataIndex = informationalVersion.IndexOf('+');
            return buildMetadataIndex < 0 ? informationalVersion : informationalVersion.Substring(0, buildMetadataIndex);
        }

        /// <summary>
        /// Names computed once per closed generic, so a dispatch never builds a span name or a type name.
        /// </summary>
        /// <typeparam name="TMessage">The compile-time type of the message being dispatched.</typeparam>
        private static class DispatchNames<TMessage>
        {
            internal static readonly string MessageTypeName = typeof(TMessage).FullName;
            internal static readonly string SpanName = BuildSpanName(typeof(TMessage));
        }
    }
}
