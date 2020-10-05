using Chatter.CQRS.Context;
using System;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Contains contextual information about an error that occurred while a message was being received
    /// </summary>
    public sealed class FailureContext : IContainContext
    {
        /// <summary>
        /// Creates an object containing contextual information about an error that occurred while a message was being received
        /// </summary>
        /// <param name="errorDetails">The details of the error</param>
        /// <param name="errorDescription">The description of the error</param>
        public FailureContext(string errorDetails, string errorDescription)
        {
            if (string.IsNullOrWhiteSpace(errorDetails))
            {
                throw new ArgumentException("An failure reason is required when an error occurs.", nameof(errorDetails));
            }

            if (string.IsNullOrWhiteSpace(errorDescription))
            {
                throw new ArgumentException("An failure description is required when an error occurs.", nameof(errorDescription));
            }

            ErrorDetails = errorDetails;
            ErrorDescription = errorDescription;
        }

        /// <summary>
        /// The details of the error
        /// </summary>
        public string ErrorDetails { get; }
        /// <summary>
        /// The description of the error
        /// </summary>
        public string ErrorDescription { get; }

        public ContextContainer Container { get; } = new ContextContainer();

        public override string ToString() => $"{ErrorDetails}:\n{ErrorDescription}";
    }
}
