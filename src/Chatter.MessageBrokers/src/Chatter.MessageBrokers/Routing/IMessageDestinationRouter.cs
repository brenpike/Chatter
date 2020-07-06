using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public interface IMessageDestinationRouter
    {
    }

    public interface IMessageDestinationRouter<in TDestinationRouterContext> where TDestinationRouterContext : IContainDestinationToRouteContext
    {
        Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, TDestinationRouterContext destinationRouterContext);
    }
}
