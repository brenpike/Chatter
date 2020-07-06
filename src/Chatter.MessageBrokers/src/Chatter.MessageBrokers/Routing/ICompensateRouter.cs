using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public interface ICompensateRouter : IMessageDestinationRouter<CompensateContext>
    {
        Task Route(string compensateDestinationPath, InboundBrokeredMessage inboundMessage, MessageBrokerContext messageContext, TransactionContext transactionContext, string reason, string errorDescription);
    }
}