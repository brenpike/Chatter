using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    public class BrokeredMessageOutbox<TContext> : IBrokeredMessageOutbox, IPollableOutboxStore, IUnitOfWork where TContext : DbContext
    {
        private readonly TContext _context;
        private readonly ILogger<BrokeredMessageOutbox<TContext>> _logger;
        private readonly UnitOfWork<TContext> _unitOfWork;
        private readonly int? _outboxPollBatchSize;
        private readonly int? _outboxMaxDispatchAttempts;

        IPersistanceTransaction IUnitOfWork.CurrentTransaction => _unitOfWork.CurrentTransaction;
        bool IUnitOfWork.HasActiveTransaction => _unitOfWork.HasActiveTransaction;

        /// <summary>
        /// Creates an outbox whose poll takes every unprocessed message that is due, however many there are, and
        /// applies no attempt ceiling.
        /// </summary>
        /// <remarks>
        /// This is the legacy uncapped path, kept for a caller that constructs the outbox itself. The container
        /// resolves the overload taking <see cref="ReliabilityOptions"/>, so a Chatter-configured host polls the
        /// Outbox Poll Batch rather than the whole backlog.
        /// <para>
        /// INVARIANT: only the cap and the ceiling are options-dependent. The due clause is not, so a message whose
        /// dispatch keeps failing stops holding its place at the head of every poll here too. Oracle:
        /// <c>WhenGettingUnprocessedMessages.MustDueGateWithoutReliabilityOptions</c>.
        /// </para>
        /// </remarks>
        public BrokeredMessageOutbox(TContext context, ILoggerFactory loggerFactory)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _ = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            _logger = loggerFactory.CreateLogger<BrokeredMessageOutbox<TContext>>();
            _unitOfWork = new UnitOfWork<TContext>(context, loggerFactory.CreateLogger<UnitOfWork<TContext>>());
            _outboxPollBatchSize = null;
            _outboxMaxDispatchAttempts = null;
        }

        /// <summary>
        /// Creates an outbox whose poll takes at most <see cref="ReliabilityOptions.OutboxPollBatchSize"/> messages.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The configured batch size is below 1</exception>
        /// <remarks>
        /// INVARIANT: a batch size below 1 is refused here rather than clamped. It names a poll that takes nothing,
        /// which leaves the outbox undrained for as long as the host runs, and a store that quietly substituted a
        /// number the operator did not configure would hide that. <see cref="ReliabilityOptionsBuilder"/> already
        /// refuses such a value while the options are being built, so this guard answers only an options instance
        /// built by hand - and it answers it at construction, before any message can be missed.
        /// </remarks>
        public BrokeredMessageOutbox(TContext context, ILoggerFactory loggerFactory, ReliabilityOptions reliabilityOptions)
            : this(context, loggerFactory)
        {
            _ = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));

            if (reliabilityOptions.OutboxPollBatchSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(reliabilityOptions),
                                                      reliabilityOptions.OutboxPollBatchSize,
                                                      $"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxPollBatchSize)} must be at least 1 message.");
            }

            _outboxPollBatchSize = reliabilityOptions.OutboxPollBatchSize;
            _outboxMaxDispatchAttempts = reliabilityOptions.OutboxMaxDispatchAttempts;
        }

        /// <remarks>
        /// INVARIANT: the 'still unprocessed' predicate the claiming update emits does not come from this query.
        /// <see cref="UpdateProcessedDate(OutboxMessage, CancellationToken)"/> states the claim's original
        /// ProcessedFromOutboxAtUtc - which OutboxMessageConfiguration maps to a concurrency token - as null, so a
        /// message this poll returns emits that predicate whether the poll tracked it or not. Oracle:
        /// <c>WhenUpdatingProcessed.MustStateTheClaimAsUnprocessedWhenTheMessageIsDetached</c>. The claim's behaviour
        /// under concurrency is proven over a real database by
        /// Integration/WhenClaimingOutboxConcurrentlyOnSqlServer.
        /// <para>
        /// INVARIANT: the due clause and the attempt ceiling are part of the QUERY, so a message that is not due
        /// never leaves the database. Both clauses also sit BEFORE the OrderBy/Take: filtering rows the take has
        /// already claimed shrinks the batch rather than filling it from behind, so as few as
        /// <see cref="ReliabilityOptions.OutboxPollBatchSize"/> held-back messages would leave this poll returning
        /// nothing at all. Moving the due clause into a Where AFTER the Take reddens THREE facts (measured):
        /// <c>WhenGettingUnprocessedMessages.MustSpendNoBatchSlotOnAMessageThatIsNotDue</c>, plus
        /// <c>Integration.WhenDrainingPastAPermanentlyFailingRowOnSqlServer.MustDispatchEachPermanentlyFailingMessageExactlyOnceInOneDrain</c>
        /// and <c>.MustDispatchTheMessageBehindAFullBatchOfPermanentlyFailingMessages</c>, which are the two facts
        /// that fill a whole batch with held-back messages over a real server and so feel the shrinking batch
        /// directly. Moving the ceiling clause there instead reddens
        /// <c>WhenGettingUnprocessedMessages.MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling</c> and
        /// nothing else (measured). Both mutations leave the core <c>Chatter.MessageBrokers</c> suite green.
        /// </para>
        /// <para>
        /// INVARIANT: both clauses TRANSLATE, so a message that is not due never leaves the database. Every other
        /// fact in <c>WhenGettingUnprocessedMessages</c> runs on the InMemory provider, which evaluates a Where in
        /// process and so cannot tell a translated predicate from one EF would refuse. Oracle:
        /// <c>WhenGettingUnprocessedMessages.MustTranslateTheDueGateAndTheCeilingToSqlOverARelationalProvider</c>,
        /// which runs this poll over SQLite. Rewriting the due clause as a call to a static helper - a shape EF
        /// refuses - reddens it and EIGHTEEN further facts in this suite, 23 test cases in all once the
        /// two-case theories are counted, and nothing in the core <c>Chatter.MessageBrokers</c> suite (measured on
        /// both target frameworks). Everything that polls through this query over a relational provider falls with
        /// it: six of the eight <c>WhenClaimingForDispatch</c> facts - all but its two refusal facts, which get the
        /// refusal they expect either way - both <c>WhenReclaimingAfterAFailedClaimOverSqlite</c> facts, all four
        /// facts of each of the two SQL Server integration fixtures that drain the outbox
        /// (<c>WhenArbitratingOutboxDrainsOnSqlServer</c> and
        /// <c>WhenDrainingPastAPermanentlyFailingRowOnSqlServer</c>),
        /// <c>WhenUpdatingProcessed.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed</c> and
        /// <c>WhenConfiguringReliabilityBehaviors.MustResolveOutboxThroughTheBatchSizeAwareConstructor</c>. The
        /// mutation is nowhere near exclusive, because translation is a property of the query every relational
        /// caller shares rather than of one fact - which is also why the one fact named above is the oracle worth
        /// citing: it is the only one that fails for THIS reason rather than as collateral.
        /// </para>
        /// <para>
        /// NOTE: no oracle pins the boundary between <c>&lt;=</c> and <c>&lt;</c> against now. The instant is read
        /// from the wall clock inside this method, so no test can name a message due at exactly it; the two differ
        /// only for a message whose next attempt lands on that very tick, and one a tick early is taken by the
        /// following poll.
        /// </para>
        /// </remarks>
        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var outbox = _context.Set<OutboxMessage>();
            var selectable = outbox.Where(message => message.ProcessedFromOutboxAtUtc == null
                                                     && (message.NextAttemptAtUtc == null || message.NextAttemptAtUtc <= now));

            if (_outboxMaxDispatchAttempts is not null)
            {
                var maxDispatchAttempts = _outboxMaxDispatchAttempts.Value;
                selectable = selectable.Where(message => message.DispatchAttempts < maxDispatchAttempts);
            }

            var unprocessed = selectable.OrderBy(message => message.SentToOutboxAtUtc);

            if (_outboxPollBatchSize is null)
            {
                return await unprocessed.ToListAsync(cancellationToken).ConfigureAwait(false);
            }

            return await unprocessed.Take(_outboxPollBatchSize.Value).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <remarks>
        /// INVARIANT: this lookup carries neither the cap, the due clause nor the attempt ceiling the poll above
        /// applies. Its caller runs it once per unit of work with no re-poll loop behind it, so any of the three
        /// would drop a message nothing would ever come back for rather than deferring it. Oracle for the due
        /// clause and the ceiling:
        /// <c>WhenGettingUnprocessedMessages.MustNeitherDueGateNorCeilingTheUnprocessedBatch</c>; adding the due
        /// clause here reddens it and nothing else - measured across both suites and both target frameworks.
        /// NO oracle pins the absence of a CAP - the fact
        /// stages a single message, so a Take would still return it - and none is added here, because a fact that
        /// staged more would pin a number this method does not otherwise name.
        /// </remarks>
        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId, CancellationToken cancellationToken = default)
        {
            var outbox = _context.Set<OutboxMessage>();
            return await outbox.Where(message => message.ProcessedFromOutboxAtUtc == null && message.BatchId == batchId).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default)
        {
            var set = _context.Set<OutboxMessage>();
            foreach (var message in outboxMessages)
            {
                UpdateProcessedDate(set, message);
            }

            return Task.CompletedTask;
        }

        public Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default)
        {
            UpdateProcessedDate(_context.Set<OutboxMessage>(), outboxMessage);
            return Task.CompletedTask;
        }

        /// <remarks>
        /// INVARIANT: the claim states its own 'still unprocessed' predicate rather than inheriting one from the
        /// change tracker. OutboxMessageConfiguration maps ProcessedFromOutboxAtUtc as a concurrency token, so EF
        /// emits the entry's ORIGINAL value as the predicate on the claiming update. A tracked message keeps the
        /// null it was loaded with, but <c>Update</c> on a DETACHED message sets original from current, which would
        /// make the predicate read '= the stamp just written', match no row, and leave a message the broker already
        /// took unprocessed for the next poll to publish again. Stating the original as null emits the same
        /// predicate either way. Oracle:
        /// <c>WhenUpdatingProcessed.MustStateTheClaimAsUnprocessedWhenTheMessageIsDetached</c>; dropping the
        /// original-value statement reddens it and nothing else - measured across both suites and both target
        /// frameworks.
        /// </remarks>
        private void UpdateProcessedDate(DbSet<OutboxMessage> outbox, OutboxMessage outboxMessage)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            var entry = outbox.Update(outboxMessage);
            entry.OriginalValues[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)] = null;
        }

        /// <remarks>
        /// INVARIANT: this writes through ExecuteUpdateAsync rather than through the change tracker, so it carries
        /// neither the pending state of the tracked message nor the ProcessedFromOutboxAtUtc concurrency token
        /// OutboxMessageConfiguration maps. Its caller reaches here after a dispatch failed. Were the message it is
        /// handed still tracked holding the claim stamp as its CURRENT value against a null original, a tracked
        /// SaveChangesAsync would re-emit the very 'still unprocessed' predicate that just failed - and would commit
        /// the claim if it now matched. Oracle:
        /// <c>WhenUpdatingProcessed.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed</c>;
        /// recording through <c>Update</c> plus <c>SaveChangesAsync</c> instead reddens it and nothing else -
        /// measured across both suites and both target frameworks. The other two facts here survive that
        /// mutation, because their message is clean and a tracked save reaches the row just as well.
        /// <para>
        /// INVARIANT: it is also a write that must LAND, which is the second reason it is not staged. The
        /// transaction that carried the claim has already rolled back and there is no unit of work left to commit
        /// with, so a staged attempt would be discarded and the message would come back due now with nothing spent.
        /// Oracle: <c>WhenUpdatingProcessed.MustCountOneMoreDispatchAttemptOnTheStoredRow</c>, which reads the row
        /// back through a second context rather than off the tracked instance. Staging the write without saving
        /// reddens SEVEN facts (measured): that one and BOTH others here - this is a claim all three pin rather
        /// than one, since every one of them reads the stored row - and all four
        /// <c>Integration.WhenDrainingPastAPermanentlyFailingRowOnSqlServer</c> facts, which are exactly the
        /// drains that depend on a spent attempt surviving to the next poll.
        /// </para>
        /// <para>
        /// INVARIANT: the predicate is the message's own <see cref="OutboxMessage.Id"/>, so the write touches
        /// exactly the message it was handed; a wider one would spend an attempt, and push out a next attempt
        /// instant, on messages whose dispatch never failed. Oracle:
        /// <c>WhenUpdatingProcessed.MustRecordTheAttemptOnlyOnTheMessageItWasHanded</c>; dropping the Where reddens
        /// THREE facts (measured): that one, and
        /// <c>Integration.WhenDrainingPastAPermanentlyFailingRowOnSqlServer.MustDispatchEachPermanentlyFailingMessageExactlyOnceInOneDrain</c>
        /// together with <c>.MustDispatchTheMessageBehindAFullBatchOfPermanentlyFailingMessages</c>, which hold
        /// several messages at once and so notice an attempt spent on the wrong ones.
        /// </para>
        /// <para>
        /// NOTE: the count is incremented in the database rather than from the count the supplied message carries,
        /// so a stale in-memory value cannot undo an attempt another host recorded. No test pins that: the
        /// difference shows only when two hosts record against the same message, and the suite drives one.
        /// </para>
        /// <para>
        /// NOTE: a rolled-back claim does not outlive the unit of work that staged it. A unit of work that BEGAN
        /// the transaction it rolls back reconciles the context's change tracker with the rollback, so no LATER
        /// unit of work on the same <see cref="DbContext"/> emits what an EARLIER one left staged. Where the unit
        /// of work did NOT begin the transaction it leaves the tracker as it found it, because the caller owns both
        /// that transaction and the state staged into it. See
        /// docs/adr/0034-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md. This method depends
        /// on neither case: it reaches the row through ExecuteUpdateAsync whatever the tracker holds, which is what
        /// <c>WhenUpdatingProcessed.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed</c> pins.
        /// </para>
        /// <para>
        /// NOTE: one outcome on the CONCURRENCY path follows from the original value
        /// <see cref="UpdateProcessedDate(OutboxMessage, CancellationToken)"/> states, on a path this method is not
        /// on. After a clean <see cref="DbUpdateConcurrencyException"/> - one the compensation below resynced to the
        /// stored row and left Unchanged - the re-claim's 'still unprocessed' predicate matches no row and reports
        /// the message unclaimed, so the row keeps the winning host's
        /// <see cref="OutboxMessage.ProcessedFromOutboxAtUtc"/> and spends one attempt, rather than overwriting the
        /// winner's instant and spending none. Both end states are a processed row the due gate never selects again,
        /// so neither loses nor duplicates a message. <c>OutboxProcessor</c> cannot tell the two apart: it lives in
        /// <c>Chatter.MessageBrokers</c> and cannot name <see cref="DbUpdateConcurrencyException"/>, an EF type. NO
        /// oracle separates the two outcomes.
        /// </para>
        /// </remarks>
        public Task RecordDispatchAttempt(OutboxMessage outboxMessage, DateTime nextAttemptAtUtc, CancellationToken cancellationToken = default)
        {
            _ = outboxMessage ?? throw new ArgumentNullException(nameof(outboxMessage));

            var outboxMessageId = outboxMessage.Id;

            return _context.Set<OutboxMessage>()
                           .Where(message => message.Id == outboxMessageId)
                           .ExecuteUpdateAsync(setters => setters
                                                   .SetProperty(message => message.DispatchAttempts, message => message.DispatchAttempts + 1)
                                                   .SetProperty(message => message.NextAttemptAtUtc, nextAttemptAtUtc),
                                               cancellationToken);
        }

        /// <remarks>
        /// INVARIANT: the drain claim is ONE statement, so the read of the observed value and the write of the
        /// claimed one cannot be separated by another drain. A read-then-write pair would let two drains both find
        /// the observed value and both go on to dispatch the same message. Oracle:
        /// <c>WhenClaimingForDispatch.MustGrantTheDrainClaimToOnlyOneOfTwoDrainsThatObservedTheSameNextAttempt</c>;
        /// dropping the observed-value conjunct reddens it and the three other facts that name an observed value -
        /// <c>MustRefuseTheDrainClaimAgainstAStaleObservedNextAttempt</c>,
        /// <c>MustCompareANullObservedNextAttemptAsIsNull</c> and
        /// <c>MustCompareANonNullObservedNextAttemptAsAParameter</c> - and nothing else: 4 reddened, measured
        /// across both suites and both target frameworks. The mutation is not exclusive because the conjunct is
        /// the whole compare-and-set, which every one of those facts reads.
        /// <para>
        /// INVARIANT: the predicate seeks a SINGLE ROW by <see cref="OutboxMessage.Id"/>, which is the outbox's
        /// primary key under <see cref="OutboxMessageConfiguration"/>. A drain must not push out the next attempt
        /// instant of a message it never polled, and - over a server that locks what a statement touches - the
        /// arbitration a losing drain waits on is confined to the one row only because the seek is. A range or scan
        /// predicate takes locks beyond that one row, and nothing here says what a drain blocked behind THOSE locks
        /// would do.
        /// Oracle: <c>WhenClaimingForDispatch.MustClaimOnlyTheMessageItWasHanded</c>; dropping the Id conjunct
        /// reddens it and one more (measured):
        /// <c>Integration.WhenArbitratingOutboxDrainsOnSqlServer.MustLeaveADifferentRowClaimableWhileADrainIsBlockedOnTheRacedRow</c>,
        /// in both of its cases. That second one is the lock half of this claim - it pins over a real server that
        /// a drain blocked on the raced row leaves a DIFFERENT row claimable, which is only true while the seek
        /// confines the locks.
        /// </para>
        /// <para>
        /// INVARIANT: the claim carries no DUE clause, unlike
        /// <see cref="GetUnprocessedMessagesFromOutbox(CancellationToken)"/>. The claim it writes is invisible to
        /// every other connection until this unit of work commits, so a drain cannot decide from the claimed instant
        /// alone whether a message is still being tried; the compare-and-set on the observed value is what arbitrates
        /// instead. NO oracle pins its absence: a due clause would refuse only a message claimed into the future by
        /// a drain still running, which is the concurrent case SQLite cannot stage.
        /// </para>
        /// <para>
        /// INVARIANT: the observed value is compared as a NULLABLE parameter, so a null one emits a literal
        /// <c>IS NULL</c> rather than an equality against a null parameter, which matches no row in SQL. A staged
        /// message carries no next attempt instant at all, so that is the commonest claim there is and refusing it
        /// would leave the outbox undrained. Oracle:
        /// <c>WhenClaimingForDispatch.MustCompareANullObservedNextAttemptAsIsNull</c>, which captures the emitted
        /// SQL rather than inferring the shape from the outcome, and its counterpart
        /// <c>MustCompareANonNullObservedNextAttemptAsAParameter</c>. Both read the PREDICATE alone rather than the
        /// whole statement, because the SET clause assigns the same column from a parameter too and a
        /// statement-wide search would be satisfied by the assignment. The only mutation measured to redden them is
        /// dropping the conjunct outright, above. Rewriting the comparison as <c>object.Equals(...)</c> reddens
        /// NOTHING - re-measured across both suites and both target frameworks, with the compiled assemblies
        /// checked to be newer than the edit, because a green run is the one result a stale binary can fake. EF
        /// translates that shape to the identical null-safe SQL, so which of the two is written here is a free
        /// choice and NO oracle separates them.
        /// </para>
        /// <para>
        /// INVARIANT: only a message that is still unprocessed may be claimed, so a message another drain dispatched
        /// between this drain's poll and this claim is refused rather than published a second time. Oracle:
        /// <c>WhenClaimingForDispatch.MustRefuseTheDrainClaimOnAMessageAlreadyProcessed</c>; dropping the
        /// ProcessedFromOutboxAtUtc conjunct reddens it and nothing else - measured across both suites and both
        /// target frameworks.
        /// </para>
        /// <para>
        /// INVARIANT: a granted claim is written onto the supplied instance as well as the row, because this
        /// statement writes outside the change tracker - the same reason
        /// <see cref="RecordDispatchAttempt(OutboxMessage, DateTime, CancellationToken)"/> above does it. Without
        /// the write-through the tracked message would still carry the instant the poll loaded, and a later
        /// <c>Update</c>, which marks EVERY property modified, would restate that stale instant and revert the claim
        /// inside the very transaction that took it. Oracle:
        /// <c>WhenClaimingForDispatch.MustWriteTheGrantedClaimOntoTheSuppliedMessage</c>; deleting the write-through
        /// reddens it and nothing else - measured across both suites and both target frameworks.
        /// </para>
        /// <para>
        /// NOTE: this method neither opens a transaction nor saves. ExecuteUpdateAsync enlists in whatever
        /// transaction the caller has open, and the caller takes this claim inside the drain's unit of work - which
        /// is what leaves a losing drain waiting on the row rather than reading round it. Oracle:
        /// <c>WhenClaimingForDispatch.MustEnlistTheClaimInTheAmbientTransaction</c>, which abandons the unit of work
        /// after the claim is granted and reads the row back unchanged, having first checked through an interceptor
        /// that a statement genuinely reached the database. That oracle pins EF's behaviour rather than a choice
        /// made here: no mutation of this method can detach the statement from the context's current transaction, so
        /// none reddens it exclusively - it is here to catch that property of EF changing under this claim. NO
        /// oracle in this project pins the WAITING itself: that needs two connections contending over one row, which
        /// the single-connection SQLite harness every fact here uses cannot stage.
        /// </para>
        /// </remarks>
        public async Task<bool> TryClaimForDispatch(OutboxMessage outboxMessage, DateTime? observedNextAttemptAtUtc, DateTime claimedNextAttemptAtUtc, CancellationToken cancellationToken = default)
        {
            _ = outboxMessage ?? throw new ArgumentNullException(nameof(outboxMessage));

            var outboxMessageId = outboxMessage.Id;

            var claimedRows = await _context.Set<OutboxMessage>()
                                            .Where(message => message.Id == outboxMessageId
                                                              && message.ProcessedFromOutboxAtUtc == null
                                                              && message.NextAttemptAtUtc == observedNextAttemptAtUtc)
                                            .ExecuteUpdateAsync(setters => setters
                                                                    .SetProperty(message => message.NextAttemptAtUtc, claimedNextAttemptAtUtc),
                                                                cancellationToken)
                                            .ConfigureAwait(false);

            if (claimedRows == 0)
            {
                return false;
            }

            outboxMessage.NextAttemptAtUtc = claimedNextAttemptAtUtc;

            return true;
        }

        public async Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            var outbox = _context.Set<OutboxMessage>();

            foreach (var obm in outboundBrokeredMessages)
            {
                await SendToOutboxImpl(outbox, obm, transactionContext, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SendToOutboxImpl(DbSet<OutboxMessage> outbox, OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            var currentTransaction = transactionContext?.Container.GetOrDefault<IPersistanceTransaction>();
            Guid transactionId = currentTransaction?.TransactionId ?? Guid.Empty;

            var outboxMessage = new OutboxMessage
            {
                MessageId = outboundBrokeredMessage.MessageId,
                MessageContext = Chatter.MessageBrokers.ChatterJson.Serialize(outboundBrokeredMessage.MessageContext),
                Destination = outboundBrokeredMessage.Destination,
                MessageBody = outboundBrokeredMessage.Stringify(),
                MessageContentType = outboundBrokeredMessage.ContentType,
                SentToOutboxAtUtc = DateTime.UtcNow,
                ProcessedFromOutboxAtUtc = null,
                BatchId = transactionId
            };

            _logger.LogTrace($"Outbox message created. MessageId: '{outboxMessage.MessageId}', BatchId: {outboxMessage.BatchId}");

            await outbox.AddAsync(outboxMessage, cancellationToken).ConfigureAwait(false);

            _logger.LogTrace($"Outbox message added to outbox. MessageId: '{outboxMessage.MessageId}', BatchId: {outboxMessage.BatchId}");
        }

        async Task IUnitOfWork.ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            try
            {
                await _unitOfWork.ExecuteAsync(cf => operation(cf), transactionContext, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ce) when (ce.Entries.Any(e => e.Entity is OutboxMessage))
            {
                foreach (var entry in ce.Entries)
                {
                    if (entry.Entity is OutboxMessage)
                    {
                        // INVARIANT: compensation is best-effort diagnostics only. Every failure it can raise - the
                        // database read on a degraded connection, a cancelled token, a rejected state change - is
                        // swallowed so that the DbUpdateConcurrencyException rethrown below stays the reported cause.
                        // The catch is deliberately broad: narrowing it to today's exception types would re-admit the
                        // masking on tomorrow's.
                        try
                        {
                            var dbVal = await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false);
                            if (dbVal is null)
                            {
                                _logger.LogWarning(ce, "Conflicted outbox message row was deleted from the outbox, nothing to resync");
                                continue;
                            }

                            var processedTime = dbVal[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)];
                            var messageId = dbVal[nameof(OutboxMessage.Id)];

                            _logger.LogWarning(ce, $"Outbox message with id '{messageId}' was already processed at '{processedTime}'");

                            entry.OriginalValues.SetValues(dbVal);
                            entry.State = EntityState.Unchanged;
                        }
                        catch (Exception compensationFailure)
                        {
                            _logger.LogWarning(compensationFailure, "Failed to resync a conflicted outbox message after a concurrency conflict; reporting the concurrency conflict instead");
                        }
                    }
                }

                throw;
            }
        }
    }
}
