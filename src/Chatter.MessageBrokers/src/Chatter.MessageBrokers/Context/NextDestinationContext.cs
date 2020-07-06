using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    public sealed class NextDestinationContext : DestinationRouterContext
    {
        public NextDestinationContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, ContextContainer container = null)
            : base(destinationPath, destinationMessageCreator, container)
        { }

        public NextDestinationContext SetDestinationMessageCreator(Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator)
        {
            return new NextDestinationContext(this.DestinationPath, destinationMessageCreator, this.Container);
        }
    }
}
