using Chatter.CQRS.Context;

namespace Chatter.MessageBrokers.Routing.Context
{
    public interface IContainRoutingContext : IContainContext
    {
        /// <summary>
        /// The name of the destination receiver that the outbound message will be routed to
        /// </summary>
        string DestinationPath { get; }
    }
}
