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
        /// <summary>
        /// Takes one Outbox Poll Batch: at most
        /// <see cref="Chatter.MessageBrokers.Reliability.Configuration.ReliabilityOptions.OutboxPollBatchSize"/>
        /// unprocessed rows, oldest <see cref="OutboxMessage.SentToOutboxAtUtc"/> first.
        /// </summary>
        /// <remarks>
        /// A store implementing this method owes both halves of that contract. The cap is what bounds the cost of a
        /// single poll; the ordering is what keeps a row from starving behind newer ones while the backlog stays
        /// above the cap, because the cap is applied to the ordered rows rather than to an arbitrary selection.
        /// <para>
        /// The poller polls again IMMEDIATELY after a batch of the full size that carries at least one message it
        /// has not already seen during this drain, and waits
        /// <see cref="Chatter.MessageBrokers.Reliability.Configuration.ReliabilityOptions.OutboxProcessingIntervalInMilliseconds"/>
        /// otherwise, so a backlog larger than the batch size drains in one interval. An answer LARGER than the cap
        /// counts as a full batch, so it is still drained and the poller still terminates, but it defeats the bound
        /// on poll cost the cap exists for.
        /// </para>
        /// <para>
        /// A message is recognised by its (<see cref="OutboxMessage.Id"/>, <see cref="OutboxMessage.MessageId"/>)
        /// pair, so the residual obligation a store carries here is that those values are STABLE across fetches of
        /// the same row. Neither the order a store returns rows in nor which rows it keeps when more share one
        /// timestamp than the cap takes can mislead the poller. A store that fabricates a fresh identity per fetch
        /// does mislead it: every poll then looks like progress, so a batch that cannot be dispatched is re-fetched
        /// and re-dispatched until the poller's own ceiling on how long one pass may run ends it, rather than after
        /// a single wasted poll. The poller still reaches the interval wait either way.
        /// </para>
        /// </remarks>
        Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default);
        Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default);
        Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default);

        Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId, CancellationToken cancellationToken = default);
    }
}
