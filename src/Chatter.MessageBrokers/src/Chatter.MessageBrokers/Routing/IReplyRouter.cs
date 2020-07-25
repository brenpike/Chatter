using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Routes a brokered message to the reply receiver
    /// </summary>
    public interface IReplyRouter : IRouteMessages<ReplyRoutingContext>
    {
    }
}
