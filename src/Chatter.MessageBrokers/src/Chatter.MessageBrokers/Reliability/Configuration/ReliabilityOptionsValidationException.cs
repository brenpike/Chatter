using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Configuration
{
    /// <summary>
    /// Thrown by <see cref="ReliabilityOptionsBuilder.Build"/> when the built <see cref="ReliabilityOptions"/>
    /// carry values the outbox polling processor can never run with.
    /// </summary>
    public class ReliabilityOptionsValidationException : Exception
    {
        public ReliabilityOptionsValidationException(IReadOnlyList<string> violations)
            : base(BuildMessage(violations))
        {
            Violations = violations;
        }

        /// <summary>Every invalid reliability option, one entry per offending value.</summary>
        public IReadOnlyList<string> Violations { get; }

        private static string BuildMessage(IReadOnlyList<string> violations)
            => $"Reliability options are invalid: {string.Join("; and ", violations)}. Correct the fluent reliability configuration or the '{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}' configuration section.";
    }
}
