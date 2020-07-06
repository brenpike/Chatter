using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public interface ICompensationStrategy
    {
        Task Compensate(InboundBrokeredMessage inboundBrokeredMessage, string compensateReason, string compensateErrorDescription, TransactionContext transactionContext, CompensateContext compensateContext);
    }
}
