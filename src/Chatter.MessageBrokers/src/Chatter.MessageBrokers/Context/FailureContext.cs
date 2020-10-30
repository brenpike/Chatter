using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
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
        public FailureContext(InboundBrokeredMessage inbound, string errorQueueName, string errorDetails, string errorDescription, int deliveryCount, TransactionContext transactionContext)
        {
            if (string.IsNullOrWhiteSpace(errorDetails))
            {
                throw new ArgumentException("An failure reason is required when an error occurs.", nameof(errorDetails));
            }

            if (string.IsNullOrWhiteSpace(errorDescription))
            {
                throw new ArgumentException("An failure description is required when an error occurs.", nameof(errorDescription));
            }

            Inbound = inbound;
            ErrorQueueName = errorQueueName;
            ErrorDetails = errorDetails;
            ErrorDescription = errorDescription;
            DeliveryCount = deliveryCount;
            TransactionContext = transactionContext;
        }

        public InboundBrokeredMessage Inbound { get; }
        public string ErrorQueueName { get; }

        /// <summary>
        /// The details of the error
        /// </summary>
        public string ErrorDetails { get; }
        /// <summary>
        /// The description of the error
        /// </summary>
        public string ErrorDescription { get; }
        public int DeliveryCount { get; }
        public TransactionContext TransactionContext { get; }
        public ContextContainer Container { get; } = new ContextContainer();

        public override string ToString() => $"{ErrorDetails}:\n{ErrorDescription}";
    }
}
