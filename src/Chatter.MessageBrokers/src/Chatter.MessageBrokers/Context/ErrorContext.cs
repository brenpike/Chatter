using Chatter.CQRS.Context;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Contains contextual information about an error that occurred while a message was being received
    /// </summary>
    public sealed class ErrorContext : IContainContext
    {
        /// <summary>
        /// Creates an object containing contextual information about an error that occurred while a message was being received
        /// </summary>
        /// <param name="errorDetails">The details of the error</param>
        /// <param name="errorDescription">The description of the error</param>
        public ErrorContext(string errorDetails, string errorDescription)
        {
            if (string.IsNullOrWhiteSpace(errorDetails))
            {
                throw new System.ArgumentException("An error reason is required when an error occurs.", nameof(errorDetails));
            }

            if (string.IsNullOrWhiteSpace(errorDescription))
            {
                throw new System.ArgumentException("An error description is required when an error occurs.", nameof(errorDescription));
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
