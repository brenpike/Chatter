namespace Chatter.MessageBrokers.Diagnostics
{
    /// <summary>
    /// The W3C Trace Context header keys Chatter writes onto, and reads back from, a brokered message's
    /// <c>MessageContext</c> dictionary.
    /// </summary>
    /// <remarks>
    /// INVARIANT: these keys are declared HERE and never on <see cref="MessageContext"/> (ADR-0010 D5).
    /// <c>RabbitMqHeaderMarshaller</c> runs a static-constructor completeness gate that reflects every public
    /// static string field on <see cref="MessageContext"/> and throws when one lacks an explicit
    /// <c>HeaderDisposition</c>. Declaring these two keys on <see cref="MessageContext"/> would therefore
    /// (1) force a same-release <c>Chatter.MessageBrokers.RabbitMQ</c> change, coupling two packages this
    /// repository versions apart, and (2) raise a <see cref="System.TypeInitializationException"/> at the first
    /// send or receive for any application that upgrades <c>Chatter.MessageBrokers</c> without also upgrading
    /// <c>Chatter.MessageBrokers.RabbitMQ</c>. Declared outside, the marshaller treats them as non-core keys and
    /// preserves them verbatim in both directions, which is exactly the desired behaviour: the trace context
    /// rides as an ordinary application header. DO NOT "tidy" these onto <see cref="MessageContext"/>.
    /// </remarks>
    public static class TraceContextHeaders
    {
        /// <summary>
        /// The W3C <c>traceparent</c> header key, spelled exactly as it travels the wire (lowercase) so a
        /// non-Chatter producer's trace context is interoperable in both directions.
        /// See https://www.w3.org/TR/trace-context/#traceparent-header.
        /// </summary>
        public const string TraceParent = "traceparent";

        /// <summary>
        /// The W3C <c>tracestate</c> header key, spelled exactly as it travels the wire (lowercase) so a
        /// non-Chatter producer's vendor state is interoperable in both directions.
        /// See https://www.w3.org/TR/trace-context/#tracestate-header.
        /// </summary>
        public const string TraceState = "tracestate";
    }
}
