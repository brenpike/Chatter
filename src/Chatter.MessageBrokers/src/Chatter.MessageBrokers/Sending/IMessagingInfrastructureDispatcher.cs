using Chatter.MessageBrokers.Context;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IMessagingInfrastructureDispatcher
    {
        Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext);
        Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext);
    }
}
