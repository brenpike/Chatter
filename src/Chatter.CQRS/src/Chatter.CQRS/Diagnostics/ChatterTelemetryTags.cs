namespace Chatter.CQRS.Diagnostics
{
    /// <summary>
    /// The attribute names emitted by Chatter's opt-in diagnostics surface. Every call site uses these
    /// constants so a single concept is never emitted under two spellings.
    /// </summary>
    /// <remarks>
    /// Names prefixed <c>chatter.</c> are Chatter-native: no OpenTelemetry semantic convention covers
    /// in-process CQRS dispatch. The remaining names are OpenTelemetry semantic conventions pinned to
    /// specification v1.30.0 (https://github.com/open-telemetry/semantic-conventions/releases/tag/v1.30.0)
    /// and each is marked <c>Stable</c> in that release's attribute registry.
    /// </remarks>
    public static class ChatterTelemetryTags
    {
        /// <summary>
        /// The fully qualified type name of the Command, Query or Event being dispatched. Chatter-native.
        /// </summary>
        public const string MessageType = "chatter.message.type";

        /// <summary>
        /// Which dispatch path handled the message. Chatter-native; values come from <see cref="DispatchKinds"/>.
        /// </summary>
        public const string DispatchKind = "chatter.dispatch.kind";

        /// <summary>
        /// The class of error a dispatch ended with, carried as the fully qualified exception type name.
        /// OpenTelemetry semantic convention <c>error.type</c> (registry: error, Stable, semconv v1.30.0).
        /// </summary>
        public const string ErrorType = "error.type";

        /// <summary>
        /// The name of the span event carrying exception detail. OpenTelemetry semantic convention
        /// <c>exception</c> (semconv v1.30.0).
        /// </summary>
        public const string ExceptionEventName = "exception";

        /// <summary>
        /// The fully qualified exception type name on an exception event. OpenTelemetry semantic convention
        /// <c>exception.type</c> (registry: exception, Stable, semconv v1.30.0).
        /// </summary>
        public const string ExceptionType = "exception.type";

        /// <summary>
        /// The exception message on an exception event. OpenTelemetry semantic convention
        /// <c>exception.message</c> (registry: exception, Stable, semconv v1.30.0).
        /// </summary>
        public const string ExceptionMessage = "exception.message";

        /// <summary>
        /// The stringified exception, including its stack trace, on an exception event. OpenTelemetry semantic
        /// convention <c>exception.stacktrace</c> (registry: exception, Stable, semconv v1.30.0).
        /// </summary>
        public const string ExceptionStackTrace = "exception.stacktrace";

        /// <summary>
        /// The permitted values of the <see cref="DispatchKind"/> attribute, one per Chatter dispatch path.
        /// </summary>
        public static class DispatchKinds
        {
            /// <summary>A Command dispatched by the Message Dispatcher to its single handler.</summary>
            public const string Command = "command";

            /// <summary>An Event dispatched by the Message Dispatcher to zero or many handlers.</summary>
            public const string Event = "event";

            /// <summary>A Query routed by the Query Dispatcher to its handler.</summary>
            public const string Query = "query";
        }
    }
}
