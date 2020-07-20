using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public interface IBrokeredMessageOutbox
    {
        Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext);
        Task<IEnumerable<OutboxMessage>> GetUnprocessedBrokeredMessagesFromOutbox();
        Task MarkMessageAsProcessed(IEnumerable<OutboxMessage> outboxMessages);
        Task MarkMessageAsProcessed(OutboxMessage outboxMessage);
    }
}
