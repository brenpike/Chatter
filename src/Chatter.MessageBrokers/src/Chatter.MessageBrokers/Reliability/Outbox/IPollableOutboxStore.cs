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
    /// <remarks>
    /// This is a secondary facet obtained by casting the single resolved <see cref="IBrokeredMessageOutbox"/>
    /// at the consumption site — not an independent DI service. A custom reliability store must implement both
    /// <see cref="IBrokeredMessageOutbox"/> and <see cref="IPollableOutboxStore"/> on one concrete registered
    /// under <see cref="IBrokeredMessageOutbox"/>; a custom primary that does not implement this interface
    /// throws <see cref="System.InvalidCastException"/> at the poll site.
    /// </remarks>
    public interface IPollableOutboxStore
    {
        Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default);
        Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default);
        Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default);

        Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId, CancellationToken cancellationToken = default);
    }
}
