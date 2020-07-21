using Chatter.MessageBrokers.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class CompensationRoutingException : BrokeredMessageRoutingException
    {
        private readonly CompensateContext _compensateContext;

        public override DestinationRouterContext RoutingContext => _compensateContext;

        public CompensationRoutingException(CompensateContext compensateContext, Exception causeOfRoutingFailure)
            : base(compensateContext, causeOfRoutingFailure, "Routing message broker compensation message failed.")
        {
            _compensateContext = compensateContext ?? throw new ArgumentNullException(nameof(compensateContext), "A compensate context is required.");
        }
    }
}
