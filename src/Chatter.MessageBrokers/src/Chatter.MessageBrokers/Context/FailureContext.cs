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
        /// <param name="failureDescription">The details of the error</param>
        /// <param name="failureDetail">The description of the error</param>
        public FailureContext(InboundBrokeredMessage inbound, string errorQueueName, string failureDescription, Exception failure, int deliveryCount, TransactionContext transactionContext)
        {
            if (string.IsNullOrWhiteSpace(failureDescription))
            {
                throw new ArgumentException("A failure description is required when a failure occurs.", nameof(failureDescription));
            }

            Inbound = inbound;
            ErrorQueueName = errorQueueName;
            FailureDescription = failureDescription;
            Failure = failure;
            DeliveryCount = deliveryCount;
            TransactionContext = transactionContext;
        }

        public InboundBrokeredMessage Inbound { get; }
        public string ErrorQueueName { get; }

        /// <summary>
        /// The details of the error
        /// </summary>
        public string FailureDescription { get; }
        /// <summary>
        /// The description of the error
        /// </summary>
        public Exception Failure { get; }
        public int DeliveryCount { get; }
        public TransactionContext TransactionContext { get; }
        public ContextContainer Container { get; } = new ContextContainer();

        public override string ToString() => $"{FailureDescription}:\n{Failure.Message} -> {Failure.StackTrace}";
    }
}
