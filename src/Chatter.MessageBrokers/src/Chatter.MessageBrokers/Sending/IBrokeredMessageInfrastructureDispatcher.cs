using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageInfrastructureDispatcher
    {
        Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext);
    }
}
