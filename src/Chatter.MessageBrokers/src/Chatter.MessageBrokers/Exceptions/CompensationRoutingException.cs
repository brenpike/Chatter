using Chatter.MessageBrokers.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class CompensationRoutingException : BrokeredMessageRoutingException
    {
        private readonly CompensationRoutingContext _compensateContext;

        public override RoutingContext RoutingContext => _compensateContext;

        public CompensationRoutingException(CompensationRoutingContext compensateContext, Exception causeOfRoutingFailure)
            : base(compensateContext, causeOfRoutingFailure, "Routing message broker compensation message failed.")
        {
            _compensateContext = compensateContext ?? throw new ArgumentNullException(nameof(compensateContext), "A compensate context is required.");
        }
    }
}
