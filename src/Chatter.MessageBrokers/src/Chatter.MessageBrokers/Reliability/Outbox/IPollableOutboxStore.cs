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
        /// unprocessed rows that are DUE, oldest <see cref="OutboxMessage.SentToOutboxAtUtc"/> first.
        /// </summary>
        /// <remarks>
        /// A store implementing this method owes all three clauses of that contract. The cap is what bounds the cost of
        /// a single poll; the ordering is what keeps a row from starving behind newer ones while the backlog stays
        /// above the cap, because the cap is applied to the ordered rows rather than to an arbitrary selection; and
        /// the due clause is what keeps a row whose dispatch keeps failing from holding its place at the head of every
        /// batch, which at as few as
        /// <see cref="Chatter.MessageBrokers.Reliability.Configuration.ReliabilityOptions.OutboxPollBatchSize"/>
        /// such rows would leave nothing else able to be polled at all.
        /// <para>
        /// A row is DUE when its <see cref="OutboxMessage.NextAttemptAtUtc"/> is null - the value a staged row carries
        /// - or has passed. As with the cap and the ordering, this is DOCUMENTED on the method rather than enforced by
        /// the caller: nothing inspects what a store hands back for size, order or dueness.
        /// </para>
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

        /// <summary>
        /// Records that dispatch of <paramref name="outboxMessage"/> was attempted and did not succeed: its
        /// <see cref="OutboxMessage.DispatchAttempts"/> rises by one and its
        /// <see cref="OutboxMessage.NextAttemptAtUtc"/> becomes <paramref name="nextAttemptAtUtc"/>, which the caller
        /// derives from the attempt count through
        /// <see cref="Chatter.MessageBrokers.Reliability.Configuration.ReliabilityOptions"/>.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this is a default interface implementation that records NOTHING, so a third-party pollable store
        /// written against the previous shape of this interface still compiles and still satisfies the cast at the
        /// poll site. It follows the precedent <see cref="IBrokeredMessageOutbox"/> set with its single-message
        /// <c>SendToOutbox</c> overload. Oracle:
        /// <c>WhenResolvingReliabilityStores.OutboxCustomPrimaryImplementingBoth_RecordDispatchAttemptDefaultsToANoOp</c>,
        /// whose store deliberately does not implement this member; giving this body a statement that raises the
        /// supplied message's attempt count and due instant reddens that ONE fact and nothing else - measured across
        /// both suites that see this interface, the core <c>Chatter.MessageBrokers</c> one and the EntityFramework
        /// reliability one, on both target frameworks.
        /// <para>
        /// A store inheriting the default keeps the pre-existing behaviour: its rows stay at zero attempts and due now,
        /// so a message whose dispatch keeps failing is re-attempted on every poll the way it is today.
        /// </para>
        /// </remarks>
        Task RecordDispatchAttempt(OutboxMessage outboxMessage, DateTime nextAttemptAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>
        /// Takes the drain claim on <paramref name="outboxMessage"/>: a compare-and-set that moves the row's
        /// <see cref="OutboxMessage.NextAttemptAtUtc"/> from <paramref name="observedNextAttemptAtUtc"/> - the value
        /// the poll that handed this message back reported - to <paramref name="claimedNextAttemptAtUtc"/>, and
        /// answers whether THIS caller is the one that moved it.
        /// </summary>
        /// <remarks>
        /// The drain claim is a different thing from the claim the processed stamp carries
        /// (<see cref="OutboxMessage.ProcessedFromOutboxAtUtc"/>): that one records that a message was dispatched,
        /// while this one only arbitrates which of several drains running at once gets to try.
        /// <para>
        /// The observed value is an EXPLICIT PARAMETER rather than a read of
        /// <see cref="OutboxMessage.NextAttemptAtUtc"/> taken at claim time, and that is what leaves the
        /// compare-and-set honest rather than a style preference. A store may hand a poll THE STORED INSTANCES
        /// THEMSELVES - the shipped in-memory one does - so a second drain reading the observed value off the
        /// message would read the FIRST drain's claim, compare it against itself, satisfy the set, and be granted
        /// the claim too: both drains would win. The parameter pins the comparison to what the poll reported
        /// instead of to whatever the row says by the time the claim runs.
        /// </para>
        /// <para>
        /// INVARIANT: this is a default interface implementation that GRANTS, so a third-party pollable store
        /// written against the previous shape of this interface still compiles and still satisfies the cast at the
        /// poll site - the same reason <c>RecordDispatchAttempt</c> above carries a default body. Granting is the
        /// answer that keeps such a store's behaviour exactly what it is today and costs it nothing; what the store
        /// gives up in exchange is arbitration, because every drain is told it won and nothing then holds two of
        /// them off one message. Oracle:
        /// <c>WhenResolvingReliabilityStores.OutboxCustomPrimaryImplementingBoth_TryClaimForDispatchGrantsByDefault</c>,
        /// whose store deliberately does not implement this member; answering <c>false</c> from this body reddens
        /// that ONE fact and nothing else - measured across both suites that see this interface, the core
        /// <c>Chatter.MessageBrokers</c> one and the EntityFramework reliability one, on both target frameworks.
        /// </para>
        /// </remarks>
        Task<bool> TryClaimForDispatch(OutboxMessage outboxMessage, DateTime? observedNextAttemptAtUtc, DateTime claimedNextAttemptAtUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        /// <summary>
        /// Takes every unprocessed row of one staged batch. This is a lookup by batch id and is NOT an Outbox Poll
        /// Batch: neither the cap, the ordering nor the due clause above applies to it.
        /// </summary>
        /// <remarks>
        /// NOTE: it is deliberately neither capped nor due-gated. Its caller runs it ONCE per unit of work and has no
        /// re-poll loop behind it, so a cap would drop the rest of that transaction's messages for good rather than
        /// deferring them, and a due gate would drop a row nothing would come back for. Its row count is already
        /// bounded by what one handler staged.
        /// </remarks>
        Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId, CancellationToken cancellationToken = default);
    }
}
