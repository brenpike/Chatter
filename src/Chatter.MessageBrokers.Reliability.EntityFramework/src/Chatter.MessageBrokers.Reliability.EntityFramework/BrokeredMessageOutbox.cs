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
        /// INVARIANT: this query stays TRACKED. The claim staged by <see cref="UpdateProcessedDate(OutboxMessage, CancellationToken)"/>
        /// is safe under concurrency only because a tracked message keeps the ProcessedFromOutboxAtUtc it was loaded
        /// with as its original value, which OutboxMessageConfiguration maps to a concurrency token and EF therefore
        /// emits as a 'still unprocessed' predicate on the claiming update. Reading this poll with AsNoTracking would
        /// hand back a detached message whose original value IS the stamp being written, so the claim would match no
        /// row and every drain would fail. Proven over a real database by
        /// Integration/WhenClaimingOutboxConcurrentlyOnSqlServer.
        /// <para>
        /// INVARIANT: the due clause and the attempt ceiling are part of the QUERY, so a message that is not due
        /// never leaves the database. Both clauses also sit BEFORE the OrderBy/Take: filtering rows the take has
        /// already claimed shrinks the batch rather than filling it from behind, so as few as
        /// <see cref="ReliabilityOptions.OutboxPollBatchSize"/> held-back messages would leave this poll returning
        /// nothing at all. Moving the due clause into a Where AFTER the Take reddens
        /// WhenGettingUnprocessedMessages.MustSpendNoBatchSlotOnAMessageThatIsNotDue and nothing else (observed);
        /// moving the ceiling clause there reddens
        /// WhenGettingUnprocessedMessages.MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling and nothing
        /// else (observed).
        /// </para>
        /// <para>
        /// INVARIANT: both clauses TRANSLATE, so a message that is not due never leaves the database. Every other
        /// fact in <c>WhenGettingUnprocessedMessages</c> runs on the InMemory provider, which evaluates a Where in
        /// process and so cannot tell a translated predicate from one EF would refuse. Oracle:
        /// <c>WhenGettingUnprocessedMessages.MustTranslateTheDueGateAndTheCeilingToSqlOverARelationalProvider</c>,
        /// which runs this poll over SQLite. Rewriting the due clause as a call to a static helper - a shape EF
        /// refuses - reddens it, plus the two other facts that poll over a relational provider
        /// (<c>WhenUpdatingProcessed.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed</c> and
        /// <c>WhenConfiguringReliabilityBehaviors.MustResolveOutboxThroughTheBatchSizeAwareConstructor</c>), and
        /// nothing else (observed). The mutation is not exclusive because translation is a property of the query
        /// every relational caller shares, not of one fact.
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
        /// <c>WhenGettingUnprocessedMessages.MustNeitherDueGateNorCeilingTheUnprocessedBatch</c>; adding either
        /// clause here reddens it and nothing else (observed). NO oracle pins the absence of a CAP - the fact
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

        private void UpdateProcessedDate(DbSet<OutboxMessage> outbox, OutboxMessage outboxMessage)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            outbox.Update(outboxMessage);
        }

        /// <remarks>
        /// INVARIANT: this writes through ExecuteUpdateAsync rather than through the change tracker, so it carries
        /// neither the pending state of the tracked message nor the ProcessedFromOutboxAtUtc concurrency token
        /// OutboxMessageConfiguration maps. Its caller reaches here after a dispatch failed, and EF does not reset
        /// the tracker when the surrounding transaction rolls back: the message still holds the claim stamp as its
        /// CURRENT value against the null it was loaded with, so a tracked SaveChangesAsync would re-emit the very
        /// 'still unprocessed' predicate that just failed - and would commit the claim if it now matched. Oracle:
        /// <c>WhenUpdatingProcessed.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed</c>;
        /// recording through <c>Update</c> plus <c>SaveChangesAsync</c> instead reddens it and nothing else
        /// (observed) - the other two facts here survive that mutation, because their message is clean and a
        /// tracked save reaches the row just as well.
        /// <para>
        /// INVARIANT: it is also a write that must LAND, which is the second reason it is not staged. The
        /// transaction that carried the claim has already rolled back and there is no unit of work left to commit
        /// with, so a staged attempt would be discarded and the message would come back due now with nothing spent.
        /// Oracle: <c>WhenUpdatingProcessed.MustCountOneMoreDispatchAttemptOnTheStoredRow</c>, which reads the row
        /// back through a second context rather than off the tracked instance. Staging the write without saving
        /// reddens that fact and BOTH others here (observed) - this is a claim all three pin rather than one, since
        /// every one of them reads the stored row.
        /// </para>
        /// <para>
        /// INVARIANT: the predicate is the message's own <see cref="OutboxMessage.Id"/>, so the write touches
        /// exactly the message it was handed; a wider one would spend an attempt, and push out a next attempt
        /// instant, on messages whose dispatch never failed. Oracle:
        /// <c>WhenUpdatingProcessed.MustRecordTheAttemptOnlyOnTheMessageItWasHanded</c>; dropping the Where reddens
        /// it and nothing else (observed).
        /// </para>
        /// <para>
        /// NOTE: the count is incremented in the database rather than from the count the supplied message carries,
        /// so a stale in-memory value cannot undo an attempt another host recorded. No test pins that: the
        /// difference shows only when two hosts record against the same message, and the suite drives one.
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
