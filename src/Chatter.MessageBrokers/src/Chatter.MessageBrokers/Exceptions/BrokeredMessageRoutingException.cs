using Chatter.MessageBrokers.Routing.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class BrokeredMessageRoutingException : Exception
    {
        public virtual RoutingContext RoutingContext { get; }

        public BrokeredMessageRoutingException(RoutingContext destinationRouterContext, string message)
            : this(destinationRouterContext, null, message)
        {
        }

        public BrokeredMessageRoutingException(RoutingContext destinationRouterContext, Exception causeOfRoutingFailure)
            : this(destinationRouterContext, causeOfRoutingFailure, "Routing message to destination message failed.")
        {
        }

        public BrokeredMessageRoutingException(RoutingContext destinationRouterContext, Exception causeOfRoutingFailure, string message)
            : base(message, causeOfRoutingFailure)
        {
            RoutingContext = destinationRouterContext ?? throw new ArgumentNullException(nameof(destinationRouterContext), "A destination routing context is required.");
        }
    }
}
