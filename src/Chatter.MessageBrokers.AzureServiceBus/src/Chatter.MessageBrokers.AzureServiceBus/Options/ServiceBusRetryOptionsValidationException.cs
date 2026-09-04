using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    /// <summary>
    /// Thrown while building <see cref="ServiceBusOptions"/> when the exponential retry values supplied
    /// fluently through <see cref="ServiceBusOptionsBuilder.WithExponentialDelay"/>, or through the
    /// <c>RetryPolicy</c> configuration section, are values the Azure Service Bus SDK can never run with.
    /// </summary>
    public class ServiceBusRetryOptionsValidationException : Exception
    {
        public ServiceBusRetryOptionsValidationException(IReadOnlyList<string> violations)
            : base(BuildMessage(violations))
        {
            Violations = violations;
        }

        /// <summary>Every invalid retry value, one entry per offending parameter.</summary>
        public IReadOnlyList<string> Violations { get; }

        private static string BuildMessage(IReadOnlyList<string> violations)
            => $"Azure Service Bus retry options are invalid: {string.Join("; and ", violations)}. Correct the fluent WithExponentialDelay call or the 'RetryPolicy' configuration section.";
    }
}
