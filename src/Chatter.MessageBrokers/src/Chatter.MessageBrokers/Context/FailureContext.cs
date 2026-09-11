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
        /// How many times the failed delivery had been received when the failure occurred, or an out-of-band value
        /// when there is no such count — read the remarks before branching on it.
        /// </summary>
        /// <remarks>
        /// <c>-1</c> means there is no delivery being counted at all. It is the value carried when a critical receiver
        /// fault is reported to an <c>ICriticalFailureNotifier</c>, and <see cref="Inbound"/> is null alongside it.
        /// <see cref="int.MaxValue"/> is the uncountable-delivery sentinel that the default
        /// <see cref="IMessagingInfrastructureReceiver.MessageDeliveryCountAsync"/> answers with when the receiving
        /// infrastructure held no usable Receive Attempts value for the delivery, so how many times it had been
        /// received is unknown. That sentinel is NOT a reserved value: a Receive Attempts that genuinely holds
        /// <see cref="int.MaxValue"/> is returned as itself and arrives here indistinguishably. The collision is
        /// reachable rather than theoretical, because an adapter that saturates a publisher-supplied delivery-count
        /// header into <c>[0, int.MaxValue]</c> can stamp exactly this value. Both readings settle the delivery the
        /// same way — <see cref="int.MaxValue"/> sits at or above every configurable MaxReceiveAttempts, so it
        /// deadletters either way — so what the collision costs is the ability to say WHY, never the outcome.
        /// An application's registered recovery or notification action must therefore not do ARITHMETIC on this value
        /// — not subtracting from it, not computing a backoff from it, not treating it as an attempt number — because
        /// it may mean "no delivery" or "uncountable", while every such computation would answer with a number
        /// regardless.
        /// </remarks>
        public int DeliveryCount { get; }
        public TransactionContext TransactionContext { get; }
        public ContextContainer Container { get; } = new ContextContainer();

        public override string ToString() => $"{FailureDescription}:\n{Failure.Message} -> {Failure.StackTrace}";
    }
}
