using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageDispatcher
    {
        Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext);
    }
}
