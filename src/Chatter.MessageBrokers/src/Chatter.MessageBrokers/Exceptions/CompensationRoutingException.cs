using Chatter.MessageBrokers.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class CompensationRoutingException : Exception
    {
        public CompensateContext CompensateContext { get; }

        public CompensationRoutingException(CompensateContext compensateContext, Exception causeOfRoutingFailure)
            : base("Routing message broker compensation message failed.", causeOfRoutingFailure)
        {
            CompensateContext = compensateContext ?? throw new ArgumentNullException(nameof(compensateContext), "A compensate context is required.");
        }
    }
}
