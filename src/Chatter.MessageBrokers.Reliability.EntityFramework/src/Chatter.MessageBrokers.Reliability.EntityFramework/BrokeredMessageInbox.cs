using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        // TryClaimMessageIdAsync reddens both. What this type does about the commit is RECORD the claim's own
        // outcome against the transaction it flushed into, through InboxClaimRegister, which
        // PersistanceTransaction.CommitAsync reads; it still issues no Commit and no Rollback of its own. See
        // docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md,
        // docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md
        // and docs/adr/0034-an-unsettled-inbox-claim-withholds-the-commit.md.
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

            // INVARIANT: the claim is SETTLED only by the handler RETURNING, and one observation - that the handler
            // did not return - carries two consequences.
            //
            // The first is the IDENTITY MAP, and it is undone outside this type. The lookup above resolves from the
            // identity map before the store, so a claim left tracked after its transaction rolled back would be
            // found by a redelivery over this same context and suppress a message nothing ever handled. A handler
            // that throws propagates out of UnitOfWork<TContext>.ExecuteAsync, whose catch rolls the transaction
            // back and then clears that context's change tracker, which is what takes the claim back out of the
            // identity map. Oracles:
            // MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAFreshMessageId and
            // MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAnExpiredMessageId, which go red
            // when that clear is removed from ExecuteAsync's catch. This method wrapped the handler call in a catch
            // that detached the marker itself; removing that catch reddened nothing, measured on both target
            // frameworks by deleting it and running the suite. Rationale in
            // docs/adr/0035-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md.
            //
            // ExecuteAsync rolls back and clears only a transaction it began itself, so what that clear reaches is
            // exactly what the ownership refusal below admits: a claim can only ever have been staged into a
            // transaction a unit of work began. Oracle:
            // MustRefuseToClaimInsideATransactionNoUnitOfWorkBegan.
            //
            // The second is DURABILITY. The flush already pushed the claim into the transaction, where clearing the
            // change tracker cannot reach it, so a caller that CATCHES the handler's exception and returns normally
            // would otherwise leave the unit of work committing a claim for a message nothing handled. Settlement
            // is what the commit point reads, and it is granted in exactly one place - after the handler returned -
            // so a throw, an upstream swallow, a cancellation, or any early return all leave the claim unsettled
            // and the commit refused. Oracles: MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure and
            // MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed; moving the Settle call below to before the
            // handler reddens both. MustCommitTheClaimWhenTheHandlerReturns pins the grant itself, so a settlement
            // that never happened cannot pass for a refusal that is always right.
            //
            // The remaining way this context can hold a phantom claim is the handler succeeding and the unit of
            // work's own commit then failing, which ReceiveViaInbox has already returned from and cannot observe.
            // A commit that throws inside CompleteAsync lands in ExecuteAsync's catch, so the rollback and the
            // clear both run for that path; issue #512 tracks the decision on it.
            await handler().ConfigureAwait(false);

            claim.Settle();

            _logger.LogDebug("Message handler executed successfully from inbox");
        }

        // INVARIANT: the two refusals below and the flush that follows them land together and are never separated.
        // A flush outside a transaction autocommits, so a failure between that autocommit and the handler's work
        // would leave a marker suppressing a message nothing ever handled; the first guard is a runtime read of
        // `_context.Database.CurrentTransaction` that refuses that case rather than merely leaving it unlikely.
        // Oracle: MustRefuseToClaimOutsideATransaction, the only fact in the suite that goes red when that guard
        // is removed.
        //
        // INVARIANT: the second guard widens the key of that same refusal from "is there a transaction" to "does a
        // unit of work OWN this transaction". ELIMINATED CLASS: a claim flushed into a transaction whose failure
        // and whose commit this package does not control. Both protections the claim rests on - the change-tracker
        // reconciliation on UnitOfWork's rollback, and PersistanceTransaction's unsettled-claim refusal - act only
        // on a transaction a unit of work began, so an ADOPTED transaction would take the claim and leave both
        // behind. The lookup asks UnitOfWorkTransactionRegister about the ONE transaction object read below, so it
        // is a fact carried from where that transaction was begun rather than an ownership re-derived from which
        // unit of work happens to be running. Oracles: MustRefuseToClaimInsideATransactionNoUnitOfWorkBegan for
        // the refusal, MustCommitTheClaimWhenTheHandlerReturns for the grant, and
        // MustClaimInsideAUnitOfWorkNestedInAnothersTransaction for the keying, which is what keeps a nested
        // dispatch's claim admitted. Rationale on UnitOfWorkTransactionRegister.
        //
        // The claim is returned rather than a bool so the caller can name the registration it must settle when the
        // handler returns; null reports absorption, the async stand-in for an out parameter.
        //
        // INVARIANT: the transaction read below is the ONE read, captured and carried on the returned claim, so the
        // refusal, the registration and the settlement all name the same IDbContextTransaction object. Re-reading
        // _context.Database.CurrentTransaction at settlement time would consult whatever transaction the context
        // held by then, which after a nested unit of work need not be the one this claim was flushed into.
        private async Task<ClaimedMessageId> TryClaimMessageIdAsync(string messageId, InboxMessage expiredMarker, CancellationToken cancellationToken)
        {
            var transaction = _context.Database.CurrentTransaction;
            if (transaction is null)
            {
                throw new InvalidOperationException(
                    $"BrokeredMessageInbox<{typeof(TContext).Name}> refuses to claim message id '{messageId}' because '{typeof(TContext).Name}' has no active transaction. " +
                    $"The claim is written before the handler runs so that a concurrent delivery of the same message id is ordered behind it by the store, and without a " +
                    $"transaction that write would commit on its own: a failure anywhere between it and the handler's work would leave a marker suppressing a message nothing " +
                    $"ever handled. Register the inbox with WithInboxBehavior<{typeof(TContext).Name}>(), which registers the matching unit of work, or run this call inside a " +
                    $"unit of work's ExecuteAsync.");
            }

            if (!UnitOfWorkTransactionRegister.IsOwnedByAUnitOfWork(transaction))
            {
                throw new InvalidOperationException(
                    $"BrokeredMessageInbox<{typeof(TContext).Name}> refuses to claim message id '{messageId}' because no unit of work owns the transaction " +
                    $"'{typeof(TContext).Name}' is currently in. The claim is written before the handler runs, and everything that undoes it when the handler does " +
                    $"not return acts only on a transaction a unit of work began: the rollback that reconciles this context's change tracker, and the commit point " +
                    $"that withholds a commit carrying an unsettled claim. Flushed into a transaction begun elsewhere, the claim would survive its own failure - the " +
                    $"owner's rollback leaves it in this context's identity map, where the next lookup finds it and skips a message nothing ever handled. Register the " +
                    $"inbox with WithInboxBehavior<{typeof(TContext).Name}>(), which registers the matching unit of work, or run this call inside " +
                    $"IUnitOfWork.ExecuteAsync rather than inside a transaction you began yourself.");
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

                return new ClaimedMessageId(transaction, InboxClaimRegister.Open(transaction, messageId, typeof(TContext).Name));
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

        /// <summary>
        /// Pairs the transaction a claim was flushed into with the registration the commit point reads, so the
        /// settlement the handler's return grants names the transaction that claim went into.
        /// </summary>
        private sealed class ClaimedMessageId
        {
            private readonly IDbContextTransaction _transaction;
            private readonly UnsettledClaim _unsettledClaim;

            public ClaimedMessageId(IDbContextTransaction transaction, UnsettledClaim unsettledClaim)
            {
                _transaction = transaction;
                _unsettledClaim = unsettledClaim;
            }

            public void Settle() => InboxClaimRegister.Settle(_transaction, _unsettledClaim);
        }
    }
}
