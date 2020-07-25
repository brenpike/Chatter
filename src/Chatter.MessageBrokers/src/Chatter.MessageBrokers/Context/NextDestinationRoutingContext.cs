using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    public class NextDestinationRoutingContext : RoutingContext
    {
        public NextDestinationRoutingContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, ContextContainer inheritedContext = null) 
            : base(destinationPath, destinationMessageCreator, inheritedContext)
        {
        }
    }
}
