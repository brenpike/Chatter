using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.Routing
{
    public interface IReplyRouter : IMessageDestinationRouter<ReplyDestinationContext>
    {
    }
}
