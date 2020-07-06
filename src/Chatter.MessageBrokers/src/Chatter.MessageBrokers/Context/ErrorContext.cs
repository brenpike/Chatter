using Chatter.CQRS.Context;

namespace Chatter.MessageBrokers.Context
{
    public sealed class ErrorContext : IContainContext
    {
        public ErrorContext(string errorReason, string errorDescription)
        {
            if (string.IsNullOrWhiteSpace(errorReason))
            {
                throw new System.ArgumentException("An error reason is required when an error occurs.", nameof(errorReason));
            }

            if (string.IsNullOrWhiteSpace(errorDescription))
            {
                throw new System.ArgumentException("An error description is required when an error occurs.", nameof(errorDescription));
            }

            ErrorDetails = errorReason;
            ErrorDescription = errorDescription;
        }

        public string ErrorDetails { get; }
        public string ErrorDescription { get; }

        public ContextContainer Container { get; } = new ContextContainer();

        public override string ToString() => $"{ErrorDetails}:\n{ErrorDescription}";
    }
}
