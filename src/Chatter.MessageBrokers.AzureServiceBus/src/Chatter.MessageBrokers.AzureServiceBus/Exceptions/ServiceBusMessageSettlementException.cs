using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Exceptions
{
    /// <summary>
    /// Represents a failure to settle (complete/abandon/dead-letter/defer) a received Azure Service Bus message.
    /// </summary>
    /// <remarks>
    /// This type exists as a distinct class, rather than reusing a generic exception, because
    /// <c>ActivityOutcome.ResolveErrorType</c> reports <c>exception.GetType().FullName</c> as the
    /// <c>error.type</c> telemetry attribute — this type name is exactly what an operator sees on a
    /// dashboard when a settlement call fails.
    ///
    /// IMPORTANT: any message passed to this exception MUST NOT contain the substrings "retry",
    /// "timeout", "time out", "rerun", "internal server error", "waiting", "wait until", or
    /// "service unavailable" (case-insensitive). <c>DefaultExceptionsPredicateProvider</c> matches
    /// those raw substrings against the exception message, and a match would cause the settlement
    /// failure to be retried to exhaustion, rewriting <c>error.type</c> to
    /// <c>MaxRetryAttemptsExceededException</c> instead of this type.
    /// </remarks>
    internal sealed class ServiceBusMessageSettlementException : Exception
    {
        public ServiceBusMessageSettlementException(string message)
            : base(message) { }

        public ServiceBusMessageSettlementException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
