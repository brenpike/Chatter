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
        /// <summary>
        /// How many times the failed delivery had been received when the failure occurred.
        /// </summary>
        /// <remarks>
        /// A value of <see cref="int.MaxValue"/> is the uncountable-delivery sentinel, NOT a real attempt count: the
        /// receiving infrastructure held no usable Receive Attempts value for this delivery, so how many times it had
        /// been received is unknown here. Such a delivery is being deadlettered on its FIRST handler error rather than
        /// retried, which is the only way a registered recovery action is handed the sentinel.
        /// An application's registered recovery action must therefore not do ARITHMETIC on this value — not
        /// subtracting from it, not computing a backoff from it, not treating it as an attempt number — because the
        /// sentinel means "uncountable", while every such computation would answer with a number regardless.
        /// </remarks>
        public int DeliveryCount { get; }
        public TransactionContext TransactionContext { get; }
        public ContextContainer Container { get; } = new ContextContainer();

        public override string ToString() => $"{FailureDescription}:\n{Failure.Message} -> {Failure.StackTrace}";
    }
}
