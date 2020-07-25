using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Routes a brokered message to the next destination receiver
    /// </summary>
    public interface INextDestinationRouter : IRouteMessages<NextDestinationRoutingContext>
    {
    }
}
