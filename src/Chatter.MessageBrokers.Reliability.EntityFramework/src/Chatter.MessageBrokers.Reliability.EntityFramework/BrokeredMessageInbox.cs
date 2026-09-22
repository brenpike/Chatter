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
        // INVARIANT: this type FLUSHES but never COMMITS. It calls SaveChangesAsync twice - once to push the claim
        // into the ambient transaction before the handler runs, once to stamp the handled time after it returns -
        // and issues no Commit and no Rollback of its own, leaving UnitOfWorkBehavior's single commit as the only
        // one, which is what keeps the marker atomic with the handler's work.
        // Oracle: MustFlushTheClaimWithoutCommittingIt, which counts commits through an IDbTransactionInterceptor;
        // committing the ambient transaction after the stamp below reddens eight facts, measured on net8.0, of
        // which that one is the oracle and the other seven are the blast radius of ending the transaction early.
        // See docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md and
        // docs/adr/0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md.
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
        /// INVARIANT: the row records which of the message id's two states it is in, and it is written into
        /// <typeparamref name="TContext"/>'s ambient transaction at the moment it changes: no row means a fresh
        /// message id, a row carrying no timestamp means a CLAIM whose handler has not completed, and a row
        /// carrying one means the handler completed at that instant. ELIMINATED CLASS: an answer to "did the
        /// handler finish?" held anywhere but the row, where the window between the write and the answer agreeing
        /// is a defect. A caller that swallows the handler's failure therefore commits a claim carrying no
        /// timestamp, and the next delivery reads it as unhandled and runs the handler again. Oracles:
        /// MustFlushAClaimCarryingNoTimestampBeforeInvokingTheHandler,
        /// MustFlushTheStampWhenTheHandlerReturnsAndCommitIt and
        /// MustCommitAnUnstampedClaimWhenACallerSwallowsTheHandlersFailure; writing the handled time at claim time
        /// instead of after the handler returns reddens four facts, measured on net8.0.
        /// The atomicity of the marker and the handler's work holds whenever every reliability extension call names the same
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
        /// docs/adr/0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md.
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

            if (existingMarker != null && IsHandledWithinTheDeduplicationWindow(existingMarker))
            {
                _logger.LogInformation($"Message with id '{messageId}' found in inbox. Message will not be handled.");
                return;
            }

            if (existingMarker != null)
            {
                _logger.LogInformation(existingMarker.ReceivedByInboxAtUtc.HasValue
                    ? $"Message with id '{messageId}' was found in the inbox but was received before the "
                      + $"deduplication window of '{_retentionOptions.InboxDeduplicationWindow}'. Message will be handled again."
                    : $"Message with id '{messageId}' was found in the inbox as a claim no handler completed. Message will be handled again.");
            }

            var claim = await ClaimMessageIdAsync(messageId, existingMarker, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Executing message handler from inbox");
            await handler().ConfigureAwait(false);
            _logger.LogDebug("Message handler executed successfully from inbox");

            // INVARIANT: the handled time is written and FLUSHED here, after the handler returned, so the only
            // record that the handler finished lives in the transaction rather than in this object. Staging the
            // stamp and leaving it for the unit of work's commit to flush would put the answer back in memory for
            // the span between the handler returning and the commit - which is the span a caller that catches the
            // handler's exception occupies. Oracle: MustFlushTheStampWhenTheHandlerReturnsAndCommitIt, which reads
            // the row back from the store before any commit and so separates a flushed stamp from a staged one;
            // deleting the SaveChangesAsync below reddens that one fact and no other, measured on net8.0.
            claim.ReceivedByInboxAtUtc = DateTime.UtcNow;
            _logger.LogTrace($"Stamping the inbox claim on message id '{messageId}' as handled at '{claim.ReceivedByInboxAtUtc}'.");
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // INVARIANT: the refusal below is a RUNTIME READ of EF's own transaction state at the moment of use, not a
        // fact this type remembered. Without an ambient transaction the flush that follows autocommits, which both
        // leaves a claim standing on its own if the process fails before the handler runs and breaks the unit of
        // work's single-commit contract. Oracle: MustRefuseToClaimOutsideATransaction, the only fact in the suite
        // that goes red when this guard is removed, measured on net8.0.
        //
        // INVARIANT: the claim is FLUSHED here rather than staged, so the row is in the transaction - and holds the
        // lock the store gives it - before the handler runs. Oracles:
        // MustFlushAClaimCarryingNoTimestampBeforeInvokingTheHandler and
        // MustInvokeHandlerAndRefreshTheMarkerWhenDeduplicationWindowHasElapsed, which read the row back from the
        // store at handler entry; deleting the SaveChangesAsync below reddens those two and no others, measured on
        // net8.0.
        //
        // INVARIANT: the claim's write is FORCED for an existing row, so the UPDATE runs even when the row already
        // carried no timestamp and the claim writes null over null. The forced write is what takes the row's own
        // lock, which is how two deliveries of one message id are ordered by the store rather than by a read both
        // could pass. No test in this suite pins the forcing: the value written is the value already there, so its
        // only observable effect is the lock and the statement that takes it.
        private async Task<InboxMessage> ClaimMessageIdAsync(string messageId, InboxMessage existingMarker, CancellationToken cancellationToken)
        {
            if (_context.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException(
                    $"BrokeredMessageInbox<{typeof(TContext).Name}> refuses to claim message id '{messageId}' because '{typeof(TContext).Name}' has no active transaction. " +
                    $"The claim is written and flushed before the handler runs so that a concurrent delivery of the same message id is ordered behind it by the store, and " +
                    $"without a transaction that flush commits on its own: the claim would stand outside the unit of work that is supposed to carry it, breaking the single " +
                    $"commit the marker and the handler's work share. Register the inbox with WithInboxBehavior<{typeof(TContext).Name}>(), which registers the matching " +
                    $"unit of work, or run this call inside a unit of work's ExecuteAsync.");
            }

            InboxMessage claim;
            if (existingMarker is null)
            {
                claim = new InboxMessage()
                {
                    MessageId = messageId,
                    ReceivedByInboxAtUtc = null
                };

                _logger.LogTrace($"Claiming message id '{messageId}' in the inbox with no date received.");
                await _inbox.AddAsync(claim, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                claim = existingMarker;
                claim.ReceivedByInboxAtUtc = null;
                _context.Entry(claim).Property(m => m.ReceivedByInboxAtUtc).IsModified = true;
                _logger.LogTrace($"Reclaiming message id '{messageId}' in the inbox with no date received.");
            }

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return claim;
        }

        // INVARIANT: this answers for the HANDLER, not for the row, so a row carrying no timestamp reports
        // not-received under either setting of the Deduplication Window. Reporting received from the row's presence
        // alone would tell a caller a message was handled while its handler was still running, or had already
        // failed. Oracles: MustNotReportReceivedForAClaimWithNoTimestampWhenDeduplicationWindowIsUnset and
        // MustNotReportReceivedForAClaimWithNoTimestampWhenDeduplicationWindowIsSet; dropping the non-null tests
        // from both queries reddens those two and no others, measured on net8.0.
        public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
        {
            var deduplicationWindow = _retentionOptions.InboxDeduplicationWindow;

            if (!deduplicationWindow.HasValue)
            {
                return _inbox.AnyAsync(m => m.MessageId == messageId && m.ReceivedByInboxAtUtc != null, cancellationToken);
            }

            var cutoffUtc = DateTime.UtcNow - deduplicationWindow.Value;

            return _inbox.AnyAsync(m => m.MessageId == messageId
                                        && m.ReceivedByInboxAtUtc != null
                                        && m.ReceivedByInboxAtUtc >= cutoffUtc,
                                   cancellationToken);
        }

        // INVARIANT: this predicate answers "was this message id HANDLED, recently enough to still suppress",
        // which is two questions, and a row carrying no timestamp fails the first one outright. A row with no
        // timestamp records a claim whose handler never completed; it has no age, so no setting of the
        // Deduplication Window can make it suppress and none can expire it either. Oracles:
        // MustInvokeHandlerForAClaimWithNoTimestampWhenDeduplicationWindowIsSet and
        // MustInvokeHandlerForAClaimWithNoTimestampWhenDeduplicationWindowIsUnset, which fix the window's two
        // settings between them. Reading no timestamp as handled - suppressing whatever the window says - reddens
        // three facts; reading it as a timestamp of DateTime.MinValue, so the window ages it out, reddens two and
        // leaves the window-is-set fact passing. Both measured on net8.0.
        //
        // INVARIANT: expiry is decided HERE, at receive time, and not by the retention purge alone. A purge is the
        // only thing that reclaims the row, but it runs on its own cadence, so a marker older than the Deduplication
        // Window would keep suppressing a legitimate redelivery until the next pass happened to reach it. A null
        // window - the default - never expires a handled marker. Oracles:
        // MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset and
        // MustInvokeHandlerAndRefreshTheMarkerWhenDeduplicationWindowHasElapsed.
        private bool IsHandledWithinTheDeduplicationWindow(InboxMessage marker)
        {
            if (!marker.ReceivedByInboxAtUtc.HasValue)
            {
                return false;
            }

            var deduplicationWindow = _retentionOptions.InboxDeduplicationWindow;

            return !deduplicationWindow.HasValue
                   || DateTime.UtcNow - marker.ReceivedByInboxAtUtc.Value <= deduplicationWindow.Value;
        }
    }
}
