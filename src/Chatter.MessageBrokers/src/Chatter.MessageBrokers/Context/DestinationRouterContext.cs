using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    public abstract class DestinationRouterContext : IContainDestinationToRouteContext
    {
        public DestinationRouterContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, ContextContainer container = null)
        {
            this.DestinationPath = destinationPath;
            this.DestinationMessageCreator = destinationMessageCreator;
            this.Container = container ?? new ContextContainer();
        }

        public string DestinationPath { get; }
        protected Func<InboundBrokeredMessage, OutboundBrokeredMessage> DestinationMessageCreator { get; }
        public ContextContainer Container { get; }

        public OutboundBrokeredMessage CreateDestinationMessage(InboundBrokeredMessage inboundBrokeredMessage)
        {
            return this.DestinationMessageCreator?.Invoke(inboundBrokeredMessage) ?? OutboundBrokeredMessage.Forward(inboundBrokeredMessage, this.DestinationPath);
        }
    }
}
