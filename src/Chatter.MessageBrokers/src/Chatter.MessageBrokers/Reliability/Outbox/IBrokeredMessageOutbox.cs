using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public interface IBrokeredMessageOutbox
    {
        Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext);
        Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext);

        Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox();
        Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages);
        Task UpdateProcessedDate(OutboxMessage outboxMessage);

        Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId);
    }
}
