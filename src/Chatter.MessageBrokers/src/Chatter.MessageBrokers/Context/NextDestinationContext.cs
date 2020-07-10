using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Contains contextual information about how a received message should be routed to the next destination
    /// </summary>
    public sealed class NextDestinationContext : DestinationRouterContext
    {
        /// <summary>
        /// Creates an object which contains contextual information about how a received message should be routed to the next destination.
        /// </summary>
        /// <param name="destinationPath">The destination message receiver to be routed to</param>
        /// <param name="destinationMessageCreator">The delegate that creates an outbound message from the received inbound message</param>
        /// <param name="inheritedContext">An optional container with additional contextual information</param>
        public NextDestinationContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, ContextContainer inheritedContext = null)
            : base(destinationPath, destinationMessageCreator, inheritedContext)
        { }
    }
}
