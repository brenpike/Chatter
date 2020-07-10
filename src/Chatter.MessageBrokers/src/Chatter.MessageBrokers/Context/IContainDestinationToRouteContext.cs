using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;

namespace Chatter.MessageBrokers.Context
{
    public interface IContainDestinationToRouteContext : IContainContext
    {
        /// <summary>
        /// The name of the destination receiver that the outbound message will be routed to
        /// </summary>
        string DestinationPath { get; }
        /// <summary>
        /// Describes how an <see cref="InboundBrokeredMessage"/> should be transformed to an <see cref="OutboundBrokeredMessage"/> when being routed
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound message that was received and is being routed to <see cref="DestinationPath"/></param>
        /// <returns>The outbound message to be sent to <see cref="DestinationPath"/></returns>
        OutboundBrokeredMessage CreateDestinationMessage(InboundBrokeredMessage inboundBrokeredMessage);
    }
}
