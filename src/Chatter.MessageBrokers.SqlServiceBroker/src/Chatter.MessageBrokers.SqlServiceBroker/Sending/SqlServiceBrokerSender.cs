using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Sending
{
    public class SqlServiceBrokerSender : IMessagingInfrastructureDispatcher
    {
        public Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
        {
            throw new NotImplementedException();
        }

        public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
        {
            throw new NotImplementedException();
        }
    }
}
