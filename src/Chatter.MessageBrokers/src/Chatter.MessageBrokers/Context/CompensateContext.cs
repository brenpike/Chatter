using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Contains contextual information about how a received message should be routed to the compensation destination
    /// </summary>
    public sealed class CompensateContext : DestinationRouterContext
    {
        /// <summary>
        /// Creates an object which contains contextual information about how a received message should be routed to the compensating destination.
        /// Assumes no <see cref="CompensateDetails"/> or <see cref="CompensateDescription"/> are available.
        /// </summary>
        /// <param name="destinationPath">The destination message receiver to be routed to</param>
        /// <param name="destinationMessageCreator">The delegate that creates an outbound message from the received inbound message</param>
        /// <param name="inheritedContext">An optional container with additional contextual information</param>
        public CompensateContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, ContextContainer inheritedContext = null)
            : this(destinationPath, destinationMessageCreator, "", "", inheritedContext)
        {
        }

        /// <summary>
        /// Creates an object which contains contextual information about how a received message should be routed to the compensating destination.
        /// </summary>
        /// <param name="destinationPath">The destination message receiver to be routed to</param>
        /// <param name="destinationMessageCreator">The delegate that creates an outbound message from the received inbound message</param>
        /// <param name="compensateDetails">The details describing the reason the compensation is occurring</param>
        /// <param name="compensateDescription">A description of the compensation</param>
        /// <param name="inheritedContext">An optional container with additional contextual information</param>
        public CompensateContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, string compensateDetails, string compensateDescription, ContextContainer inheritedContext = null)
            : base(destinationPath, destinationMessageCreator, inheritedContext)
        {
            CompensateDetails = compensateDetails;
            CompensateDescription = compensateDescription;
        }

        /// <summary>
        /// The details describing the reason the compensation is occurring
        /// </summary>
        public string CompensateDetails { get; private set; }
        /// <summary>
        /// A description of the compensation
        /// </summary>
        public string CompensateDescription { get; private set; }

        /// <summary>
        /// Sets the reason that compensation is occurring
        /// </summary>
        /// <param name="compensationDetails">The details</param>
        /// <returns>The current <see cref="CompensateContext"/> instance</returns>
        public CompensateContext SetDetails(string compensationDetails)
        {
            if (string.IsNullOrWhiteSpace(compensationDetails))
            {
                throw new ArgumentException("A reason is required when compensating.", nameof(compensationDetails));
            }

            CompensateDetails = compensationDetails;
            return this;
        }

        /// <summary>
        /// Sets the description of the compensation
        /// </summary>
        /// <param name="errorDescription">The description of the compensation</param>
        /// <returns>The current <see cref="CompensateContext"/> instance</returns>
        public CompensateContext SetDescription(string errorDescription)
        {
            if (string.IsNullOrWhiteSpace(errorDescription))
            {
                throw new ArgumentException("A description describing the error that caused the compensation is required.", nameof(errorDescription));
            }

            CompensateDescription = errorDescription;
            return this;
        }

        public override string ToString() => $"{CompensateDescription} -> {CompensateDetails}";
    }
}
