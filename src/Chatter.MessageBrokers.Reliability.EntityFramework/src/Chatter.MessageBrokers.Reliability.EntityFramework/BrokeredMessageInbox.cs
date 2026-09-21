using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    /// <summary>
    /// An inbox which keeps track of brokered messages which have been processed.
    /// </summary>
    /// <typeparam name="TContext">The DbContext where the inbox presides</typeparam>
    public class BrokeredMessageInbox<TContext> : IBrokeredMessageInbox, IInboxDeduplicator where TContext : DbContext
    {
        // INVARIANT: this type flushes but never commits. It calls SaveChangesAsync to push its claim into the
        // ambient transaction and issues no Commit of its own, leaving UnitOfWorkBehavior's single commit as the
        // only one, which is what keeps the marker atomic with the handler's work. Oracles:
        // MustFlushTheClaimWithoutCommittingForAFreshMessageId and
        // MustFlushTheRefreshedClaimWithoutCommittingForAnExpiredMessageId, which count commits through an
        // IDbTransactionInterceptor; committing the ambient transaction after the flush in
        // TryClaimMessageIdAsync reddens both. See
        // docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md and
        // docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md.
        private readonly TContext _context;
        private readonly DbSet<InboxMessage> _inbox;
        private readonly ILogger<BrokeredMessageInbox<TContext>> _logger;
        private readonly ReliabilityOptions _options;
        private readonly EntityFrameworkReliabilityOptions _retentionOptions;

        public BrokeredMessageInbox(TContext context, ILogger<BrokeredMessageInbox<TContext>> logger, ReliabilityOptions options)
            : this(context, logger, options, new EntityFrameworkReliabilityOptions())
        {
        }

        public BrokeredMessageInbox(TContext context,
                                    ILogger<BrokeredMessageInbox<TContext>> logger,
                                    ReliabilityOptions options,
                                    EntityFrameworkReliabilityOptions retentionOptions)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _retentionOptions = retentionOptions ?? throw new ArgumentNullException(nameof(retentionOptions));
            _context = context;
            _inbox = context.Set<InboxMessage>();
        }

        /// <summary>
        /// Receives a message and verifies if it's been handled previously by checking the inbox.
        /// INVARIANT: the message id is CLAIMED - staged and flushed into <typeparamref name="TContext"/>'s
        /// ambient transaction - before the handler runs, so two deliveries of the same id are ordered by the
        /// store's own primary-key lock rather than by a read that snapshot isolation lets both deliveries pass.
        /// Oracle: MustInvokeTheHandlerOnceWhenASecondDeliveryRacesTheSameMessageId, which goes red when the flush
        /// in TryClaimMessageIdAsync is moved to after the handler; within a single delivery the ordering is
        /// MustRecordTheClaimBeforeInvokingTheHandlerForAFreshMessageId.
        /// The atomicity of claim and handler holds whenever every reliability extension call names the same
        /// <typeparamref name="TContext"/>, including a lone WithInboxBehavior&lt;TContext&gt;()
        /// call, which registers the matching unit of work itself. IUnitOfWork resolves to the
        /// TContext of the last call to any of WithUnitOfWorkBehavior&lt;TContext&gt;(),
        /// WithInboxBehavior&lt;TContext&gt;(), or WithOutboxProcessingBehavior&lt;TContext&gt;();
        /// IBrokeredMessageInbox resolves to the TContext of the last
        /// WithInboxBehavior&lt;TContext&gt;(). It is void only when a later
        /// WithUnitOfWorkBehavior or WithOutboxProcessingBehavior call names a different
        /// TContext, leaving the unit of work committing a different DbContext than the one
        /// holding the marker. See
        /// docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md and
        /// docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md.
        /// </summary>
        /// <typeparam name="TMessage">The type of message being received</typeparam>
        /// <param name="message">The message being received</param>
        /// <param name="messageBrokerContext">The brokered message context received with the message</param>
        /// <param name="handler">The message handler to be executed if the message is not found within the inbox</param>
        /// <returns>An awaitable task</returns>
        public async Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> handler)
        {
            var messageId = messageBrokerContext?.BrokeredMessage?.MessageId;
            if (string.IsNullOrWhiteSpace(messageId))
            {
                _logger.LogDebug("Unable to receve message using inbox because message id is null or whitespace. Executing handler.");
                await handler().ConfigureAwait(false);
                return;
            }

            var cancellationToken = messageBrokerContext.CancellationToken;

            _logger.LogTrace($"Checking inbox for brokered message with message id '{messageId}'.");

            // The equality this lookup - and HasBeenReceived below - applies is the MessageId column's COLLATION, not
            // an ordinal comparison, because both predicates are evaluated by the database. Recorded as an accepted
            // residual, with root cause, bounds and why pinning a collation here was rejected, under "the store's
            // collation, not the application, decides message-id equality" in
            // docs/adr/0026-the-relational-inbox-decides-expiry-at-receive-so-purge-timing-cannot-suppress-a-legitimate-message.md.
            var existingMarker = await _inbox.FindAsync(new object[] { messageId }, cancellationToken).ConfigureAwait(false);

            if (existingMarker != null && !HasMarkerExpired(existingMarker))
            {
                _logger.LogInformation($"Message with id '{messageId}' found in inbox. Message will not be handled.");
                return;
            }

            if (existingMarker != null)
            {
                _logger.LogInformation($"Message with id '{messageId}' was found in the inbox but was received before the "
                                       + $"deduplication window of '{_retentionOptions.InboxDeduplicationWindow}'. Message will be handled again.");
            }

            var claim = await TryClaimMessageIdAsync(messageId, existingMarker, cancellationToken).ConfigureAwait(false);
            if (claim is null)
            {
                return;
            }

            _logger.LogDebug("Executing message handler from inbox");

            // INVARIANT: a claim whose handler threw is detached, so nothing the transaction is about to roll back
            // stays in the change tracker asserting a row the store does not carry. The lookup above resolves from
            // the identity map before the store, so a claim left tracked would be found by a redelivery over this
            // same context and suppress a message nothing ever handled. Detaching is the only undo available: a
            // successful flush has already accepted the claim's values, so the refresh branch has no pre-mutation
            // timestamp left to restore, and re-reading would run on the doomed transaction and read its own
            // uncommitted write. Oracles:
            // MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAFreshMessageId and
            // MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAnExpiredMessageId; deleting the
            // catch below reddens those two facts and no others, measured by deleting it and counting.
            // No fact pins the other way this context can hold a phantom claim: the handler succeeds and the unit
            // of work's own commit then fails, which ReceiveViaInbox has already returned from and cannot observe.
            // That path predates the claim-before-handler change and is deferred to issue #512.
            try
            {
                await handler().ConfigureAwait(false);
            }
            catch
            {
                _context.Entry(claim).State = EntityState.Detached;
                throw;
            }

            _logger.LogDebug("Message handler executed successfully from inbox");
        }

        // INVARIANT: the refusal below and the flush that follows it land together and are never separated. A
        // flush outside a transaction autocommits, so a failure between that autocommit and the handler's work
        // would leave a marker suppressing a message nothing ever handled; the guard below is a runtime read of
        // `_context.Database.CurrentTransaction` that refuses that case rather than merely leaving it unlikely.
        // Oracle: MustRefuseToClaimOutsideATransaction, the only fact
        // in the suite that goes red when the guard below is removed.
        //
        // The claim is returned rather than a bool so the caller can name the entry it must detach when the handler
        // throws; null reports absorption, the async stand-in for an out parameter.
        private async Task<InboxMessage> TryClaimMessageIdAsync(string messageId, InboxMessage expiredMarker, CancellationToken cancellationToken)
        {
            if (_context.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException(
                    $"BrokeredMessageInbox<{typeof(TContext).Name}> refuses to claim message id '{messageId}' because '{typeof(TContext).Name}' has no active transaction. " +
                    $"The claim is written before the handler runs so that a concurrent delivery of the same message id is ordered behind it by the store, and without a " +
                    $"transaction that write would commit on its own: a failure anywhere between it and the handler's work would leave a marker suppressing a message nothing " +
                    $"ever handled. Register the inbox with WithInboxBehavior<{typeof(TContext).Name}>(), which registers the matching unit of work, or run this call inside a " +
                    $"unit of work's ExecuteAsync.");
            }

            InboxMessage claim;
            if (expiredMarker is null)
            {
                claim = new InboxMessage()
                {
                    MessageId = messageId,
                    ReceivedByInboxAtUtc = DateTime.UtcNow
                };

                _logger.LogTrace($"Claiming message id '{messageId}' in the inbox with date received '{claim.ReceivedByInboxAtUtc}'.");
                await _inbox.AddAsync(claim, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                claim = expiredMarker;
                claim.ReceivedByInboxAtUtc = DateTime.UtcNow;
                _logger.LogTrace($"Refreshing the inbox claim on message id '{messageId}' with date received '{claim.ReceivedByInboxAtUtc}'.");
            }

            try
            {
                // This flushes the WHOLE change tracker, not only the claim: a dispatch nested inside another
                // handler pushes that outer handler's staged entries out early, into this same transaction. They
                // stay atomic with the claim - one transaction, one commit - but a constraint or validation error
                // on an outer entry surfaces here, at the claim, instead of at the unit of work's commit.
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return claim;
            }
            catch (DbUpdateException ex)
            {
                // DbUpdateConcurrencyException derives from DbUpdateException, so this one catch serves both
                // losers: the delivery whose INSERT hit the message id's primary key, and the delivery whose
                // in-place refresh of an expired marker matched no row.
                //
                // INVARIANT: absorption is entered only when the failing batch implicates the claim and nothing
                // else. The re-read further down runs on this same transaction, so it can report rows this
                // transaction itself wrote and has not committed. The gate is what keeps that from mattering:
                // past it the store rejected the claim's own write and only that write, so no row of this
                // transaction's sits on this message id and the re-read can only observe what some other
                // transaction committed. The failure that rules out is the re-read returning this transaction's
                // own uncommitted claim and skipping the handler. The decision therefore does not depend on EF
                // Core rolling back to the savepoint it takes around a flush, which is skipped outright when
                // SupportsSavepoints is false - MultipleActiveResultSets=true produces exactly that, logged as
                // SavepointsDisabledBecauseOfMARS. The gate holds whether DbUpdateException.Entries carries the
                // whole batch or only the failing command, because an unrelated entry failing alongside the claim
                // fails the gate under either reading, so drift in that contract degrades toward rethrow and
                // redelivery. Rationale in
                // docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md.
                //
                // What the suite pins: inverting this gate reddens
                // MustInvokeTheHandlerOnceWhenASecondDeliveryRacesTheSameMessageId and
                // MustInvokeTheHandlerOnceWhenASecondDeliveryRefreshesTheSameExpiredMessageId, and no other fact.
                // Those two pin one direction only - that a lone rejected claim is still absorbed - so deleting
                // the gate reddens nothing. No test in this suite exercises a flush with savepoints disabled.
                if (ex.Entries.Count != 1 || !ReferenceEquals(ex.Entries[0].Entity, claim))
                {
                    throw;
                }

                // The failed entry is still tracked, and re-staging or re-flushing it would write it back later,
                // so it is detached and the store is re-read as ground truth. The re-read decides the outcome, not
                // the provider's error code: a committed marker the deduplication window does not age out means
                // another delivery owns this message id, and anything else - no marker, or an expired one - is a
                // failure to report.
                _context.Entry(claim).State = EntityState.Detached;

                var committedMarker = await _inbox.FindAsync(new object[] { messageId }, cancellationToken).ConfigureAwait(false);
                if (committedMarker is null || HasMarkerExpired(committedMarker))
                {
                    throw;
                }

                _logger.LogInformation($"Message with id '{messageId}' was claimed in the inbox by a concurrent delivery. Message will not be handled.");
                return null;
            }
        }

        public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
        {
            var deduplicationWindow = _retentionOptions.InboxDeduplicationWindow;

            if (!deduplicationWindow.HasValue)
            {
                return _inbox.AnyAsync(m => m.MessageId == messageId, cancellationToken);
            }

            var cutoffUtc = DateTime.UtcNow - deduplicationWindow.Value;

            return _inbox.AnyAsync(m => m.MessageId == messageId
                                        && (m.ReceivedByInboxAtUtc == null || m.ReceivedByInboxAtUtc >= cutoffUtc),
                                   cancellationToken);
        }

        // INVARIANT: expiry is decided HERE, at receive time, and not by the retention purge alone. A purge is the
        // only thing that reclaims the row, but it runs on its own cadence, so a marker older than the Deduplication
        // Window would keep suppressing a legitimate redelivery until the next pass happened to reach it. A null
        // window - the default - never expires anything, and a marker carrying no timestamp cannot be aged, so both
        // keep suppressing.
        private bool HasMarkerExpired(InboxMessage marker)
        {
            var deduplicationWindow = _retentionOptions.InboxDeduplicationWindow;

            return deduplicationWindow.HasValue
                   && marker.ReceivedByInboxAtUtc.HasValue
                   && DateTime.UtcNow - marker.ReceivedByInboxAtUtc.Value > deduplicationWindow.Value;
        }
    }
}
