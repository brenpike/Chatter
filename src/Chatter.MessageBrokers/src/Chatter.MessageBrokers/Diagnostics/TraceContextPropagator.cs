using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Chatter.MessageBrokers.Diagnostics
{
    /// <summary>
    /// Writes W3C Trace Context onto an outbound brokered message's <c>MessageContext</c> dictionary and reads it
    /// back off an inbound one, using the .NET <see cref="DistributedContextPropagator"/> so the host's configured
    /// propagator is honoured.
    /// </summary>
    /// <remarks>
    /// INVARIANT: injection is a pure function of an EXPLICITLY PASSED <see cref="Activity"/> (ADR-0010 R2).
    /// <see cref="Inject"/> never reads <see cref="Activity.Current"/>. <see cref="Activity.Current"/> is non-null in
    /// any host running unrelated instrumentation — an ASP.NET Core request activity plus any .NET
    /// <c>ActivityListener</c> at all is enough — so keying injection off it would make an application that never
    /// opted into Chatter tracing pay propagator injection, <c>traceparent</c> string construction and dictionary
    /// writes on every message, AND would put a <c>traceparent</c> on the wire while Chatter tracing is nominally
    /// off. No Chatter <c>ActivityListener</c> means no Chatter <see cref="Activity"/>, which means a <c>null</c>
    /// argument here, which means no header and no wire change.
    /// </remarks>
    public static class TraceContextPropagator
    {
        /// <summary>
        /// The longest header value <see cref="TryExtract"/> will decode or parse. Inbound headers are external,
        /// untrusted content: a W3C <c>traceparent</c> is a fixed 55 characters and the W3C Trace Context
        /// specification bounds <c>tracestate</c> at 512 characters
        /// (https://www.w3.org/TR/trace-context/#tracestate-limits), so anything longer cannot be valid trace
        /// context and is refused rather than decoded.
        /// </summary>
        private const int MaxTraceContextValueLength = 512;

        // INVARIANT: the callbacks are cached statics, not lambdas closing over the carrier, so injecting or
        // extracting trace context allocates no per-message closure. The carrier travels through the propagator's
        // own `object carrier` argument instead.
        private static readonly DistributedContextPropagator.PropagatorSetterCallback _setTraceContextValue = SetTraceContextValue;
        private static readonly DistributedContextPropagator.PropagatorGetterCallback _getTraceContextValue = GetTraceContextValue;

        /// <summary>
        /// Writes <paramref name="activity"/>'s trace context onto <paramref name="messageContext"/>.
        /// </summary>
        /// <param name="activity">The span whose context travels with the message. A <c>null</c> activity — which is
        /// what a caller holds whenever Chatter tracing is off or the span was sampled out — returns immediately and
        /// writes nothing.</param>
        /// <param name="messageContext">The outbound message's context dictionary.</param>
        public static void Inject(Activity activity, IDictionary<string, object> messageContext)
        {
            if (activity is null || messageContext is null)
            {
                return;
            }

            DistributedContextPropagator.Current.Inject(activity, messageContext, _setTraceContextValue);
        }

        /// <summary>
        /// Reads the trace context an upstream producer wrote onto <paramref name="messageContext"/>.
        /// </summary>
        /// <param name="messageContext">The inbound message's context dictionary. Its values are external,
        /// untrusted content and are never logged by this type.</param>
        /// <param name="context">The extracted remote context, or <c>default</c> when none could be read.</param>
        /// <returns><c>true</c> when a well-formed remote trace context was read; otherwise <c>false</c>.</returns>
        /// <remarks>Never throws: a missing, over-long, wrongly-typed or malformed value yields <c>false</c> and a
        /// <c>default</c> context so a poisoned header can never fail a delivery. Callers run this inside their own
        /// off-guard; it does no guarding of its own.</remarks>
        public static bool TryExtract(IReadOnlyDictionary<string, object> messageContext, out ActivityContext context)
        {
            context = default;

            if (messageContext is null || messageContext.Count == 0)
            {
                return false;
            }

            DistributedContextPropagator.Current.ExtractTraceIdAndState(messageContext, _getTraceContextValue, out var traceParent, out var traceState);

            if (string.IsNullOrEmpty(traceParent))
            {
                return false;
            }

            return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out context);
        }

        /// <summary>
        /// OVERWRITES rather than adds: the inbound context dictionary is copied outward on the merge, forward and
        /// reply paths, so when this hop HAS trace context of its own it replaces the upstream record in place rather
        /// than leaving the upstream value behind.
        /// </summary>
        /// <remarks>
        /// The overwrite runs ONLY when <see cref="Inject"/> was handed a non-null <see cref="Activity"/>. Three
        /// reachable paths hand it <c>null</c>: (1) broker diagnostics fully off, where the routers return on their
        /// <c>BrokerDiagnostics.IsEnabled</c> guard before <see cref="Inject"/> is reached at all and the dispatcher's
        /// off path passes <c>null</c> explicitly; (2) metrics-only, where <c>BrokerDiagnostics.IsEnabled</c> is an OR
        /// across the <see cref="ActivitySource"/> AND the instruments, so an application carrying only a .NET
        /// <c>MeterListener</c> enters the instrumented path while the source has no .NET <c>ActivityListener</c> and
        /// the send span is therefore <c>null</c>; and (3) a sampled-out span with no ambient
        /// <see cref="Activity.Current"/> to fall back on. On all three the inbound <c>traceparent</c> rides out on the
        /// hop UNCHANGED, because nothing here overwrites it.
        ///
        /// That is DELIBERATE, not a defect. Stripping the stale value would put a write on the wire that the
        /// application never opted into, which is the one thing ADR-0010 R2 forbids. The outward copy also PRE-DATES
        /// the telemetry work entirely: <c>BrokeredMessageDispatcher.MergeSendOptionsWithMessageContext</c>, and the
        /// routers' by-reference reuse of the inbound context dictionary, carried the inbound context outward long
        /// before any span existed to overwrite it.
        /// </remarks>
        private static void SetTraceContextValue(object carrier, string fieldName, string fieldValue)
        {
            if (carrier is IDictionary<string, object> messageContext)
            {
                messageContext[fieldName] = fieldValue;
            }
        }

        private static void GetTraceContextValue(object carrier, string fieldName, out string fieldValue, out IEnumerable<string> fieldValues)
        {
            fieldValue = null;
            fieldValues = null;

            if (carrier is IReadOnlyDictionary<string, object> messageContext && messageContext.TryGetValue(fieldName, out var rawValue))
            {
                fieldValue = CoerceTraceContextValue(rawValue);
            }
        }

        /// <summary>
        /// Accepts the two shapes a trace-context header actually arrives in and ignores every other value type.
        /// </summary>
        /// <remarks>The <c>byte[]</c> case is the normal case on RabbitMQ, not defensive coding: a key declared
        /// outside <c>MessageContext</c> is non-core, so <c>RabbitMqHeaderMarshaller</c> preserves it verbatim and
        /// the AMQP <c>longstr</c> surfaces in .NET as <c>byte[]</c> (ADR-0010 D5).</remarks>
        private static string CoerceTraceContextValue(object rawValue)
        {
            if (rawValue is string stringValue)
            {
                return stringValue.Length > MaxTraceContextValueLength ? null : stringValue;
            }

            if (rawValue is byte[] utf8Value)
            {
                return utf8Value.Length > MaxTraceContextValueLength ? null : Encoding.UTF8.GetString(utf8Value);
            }

            return null;
        }
    }
}
