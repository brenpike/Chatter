using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    /// <summary>
    /// The relational-only outbox capability for polling-based dispatch (query unprocessed records, mark processed).
    /// Document-store providers do not implement this interface; the document tier dispatches via the change-feed
    /// Outbox Relay.
    /// </summary>
    public interface IPollableOutboxStore
    {
        Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default);
        Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default);
        Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default);

        Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId, CancellationToken cancellationToken = default);
    }
}
