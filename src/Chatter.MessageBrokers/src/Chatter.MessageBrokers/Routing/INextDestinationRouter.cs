using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Routes a brokered message to the next receiver
    /// </summary>
    public interface INextDestinationRouter : IMessageDestinationRouter<NextDestinationContext>
    {
    }
}
