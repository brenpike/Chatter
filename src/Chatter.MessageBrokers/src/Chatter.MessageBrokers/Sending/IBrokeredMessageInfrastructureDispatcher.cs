using Chatter.MessageBrokers.Context;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IBrokeredMessageInfrastructureDispatcher
    {
        Task Dispatch(IList<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext);
        Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext);
    }
}
