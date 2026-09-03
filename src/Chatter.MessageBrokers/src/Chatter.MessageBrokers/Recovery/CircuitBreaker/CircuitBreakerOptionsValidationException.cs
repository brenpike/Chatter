using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    /// <summary>
    /// Thrown by <see cref="CircuitBreakerOptionsBuilder.Build"/> when the built <see cref="CircuitBreakerOptions"/>
    /// carry values the <see cref="CircuitBreaker"/> can never run with.
    /// </summary>
    public class CircuitBreakerOptionsValidationException : Exception
    {
        public CircuitBreakerOptionsValidationException(IReadOnlyList<string> violations)
            : base(BuildMessage(violations))
        {
            Violations = violations;
        }

        /// <summary>Every invalid circuit breaker option, one entry per offending value.</summary>
        public IReadOnlyList<string> Violations { get; }

        private static string BuildMessage(IReadOnlyList<string> violations)
            => $"Circuit breaker options are invalid: {string.Join("; and ", violations)}. Correct the fluent circuit breaker configuration or the '{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}' configuration section.";
    }
}
