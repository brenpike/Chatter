using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;

namespace Chatter.MessageBrokers.Context
{
    public interface IMessageBrokerContext : IMessageHandlerContext
    {
        InboundBrokeredMessage BrokeredMessage { get; }

        INextDestinationRouter NextDestinationRouter { get; }
        IReplyRouter ReplyRouter { get; }
        ICompensateRouter CompensateRouter { get; }
    }
}
