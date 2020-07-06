using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    public sealed class CompensateContext : DestinationRouterContext
    {
        public CompensateContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, ContextContainer container = null)
            : this(destinationPath, destinationMessageCreator, "", "", container)
        {
        }

        public CompensateContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, string reason, string errorDescription, ContextContainer container = null)
            : base(destinationPath, destinationMessageCreator, container)
        {
            CompensateReason = reason;
            CompensateErrorDescription = errorDescription;
        }

        public string CompensateReason { get; }
        public string CompensateErrorDescription { get; }

        public CompensateContext SetDestinationMessageCreator(Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator)
        {
            return new CompensateContext(DestinationPath, destinationMessageCreator, CompensateReason, CompensateErrorDescription, Container);
        }

        public CompensateContext SetReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A reason is required when compensating.", nameof(reason));
            }

            return new CompensateContext(DestinationPath, DestinationMessageCreator, reason, CompensateErrorDescription, Container);
        }

        public CompensateContext SetDescription(string errorDescription)
        {
            if (string.IsNullOrWhiteSpace(errorDescription))
            {
                throw new ArgumentException("A description describing the error that caused the compensation is required.", nameof(errorDescription));
            }

            return new CompensateContext(DestinationPath, DestinationMessageCreator, CompensateReason, errorDescription, Container);
        }

        public override string ToString() => $"{CompensateErrorDescription} -> {CompensateReason}";
    }
}
