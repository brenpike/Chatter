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
    /// A custom reliability store that satisfies both <see cref="IBrokeredMessageOutbox"/> and
    /// <see cref="IPollableOutboxStore"/> must be registered under <see cref="IBrokeredMessageOutbox"/> as a
    /// single concrete at <c>Scoped</c> or <c>Singleton</c> lifetime. The framework forwards
    /// <see cref="IPollableOutboxStore"/> to the same registration at the primary's lifetime so both facets
    /// always resolve to the same instance. A <c>Transient</c> custom primary is rejected at registration time
    /// because DI has no primitive to guarantee same-instance resolution across two Transient resolutions.
    /// A consumer that registers both facets independently as separate descriptors owns ensuring the two
    /// registrations agree; the framework does not merge them.
    /// </remarks>
    public interface IPollableOutboxStore
    {
        Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default);
        Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default);
        Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default);

        Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId, CancellationToken cancellationToken = default);
    }
}
