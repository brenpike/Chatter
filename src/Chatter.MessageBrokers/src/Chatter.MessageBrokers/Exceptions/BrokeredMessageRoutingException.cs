using Chatter.MessageBrokers.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class BrokeredMessageRoutingException : Exception
    {
        public virtual DestinationRouterContext RoutingContext { get; }

        public BrokeredMessageRoutingException(DestinationRouterContext destinationRouterContext, string message)
            : this(destinationRouterContext, null, message)
        {
        }

        public BrokeredMessageRoutingException(DestinationRouterContext destinationRouterContext, Exception causeOfRoutingFailure)
            : this(destinationRouterContext, causeOfRoutingFailure, "Routing message to destination message failed.")
        {
        }

        public BrokeredMessageRoutingException(DestinationRouterContext destinationRouterContext, Exception causeOfRoutingFailure, string message)
            : base(message, causeOfRoutingFailure)
        {
            RoutingContext = destinationRouterContext ?? throw new ArgumentNullException(nameof(destinationRouterContext), "A destination routing context is required.");
        }
    }
}
