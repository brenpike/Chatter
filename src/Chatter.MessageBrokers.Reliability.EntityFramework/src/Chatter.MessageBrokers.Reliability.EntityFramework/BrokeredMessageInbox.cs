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
        // INVARIANT: ReceiveViaInbox calls DbSet.AddAsync and no SaveChangesAsync, so the relational
        // inbox never commits its own marker. The marker is committed exactly once, by
        // UnitOfWorkBehavior's single SaveChangesAsync inside the transaction, together with the
        // handler's work. This is enforced by the canonical resolved order
        // [OutboxProcessingBehavior, UnitOfWorkBehavior, InboxBehavior] and by the characterization
        // test MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId -- not by the
        // declared type of this field. Holding a DbSet<InboxMessage> buys a narrowed declared
        // surface (no _context.SaveChangesAsync(...) in this type's own vocabulary), and
        // MustNotDeclareADbContextField is a regression tripwire against reintroducing a
        // DbContext-typed field, the exact path the reverted self-save took. It does not make a
        // commit unreachable: under Microsoft.EntityFrameworkCore 10.0.0 the runtime
        // InternalDbSet<T> implements IInfrastructure<DbContext>, so the owning DbContext is still
        // obtainable from this handle. See
        // docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md.
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
            _inbox = context.Set<InboxMessage>();
        }

        /// <summary>
        /// Receives a message and verifies if it's been handled previously by checking the inbox.
        /// INVARIANT: this method adds the inbox message to the <typeparamref name="TContext"/> and
        /// never self-commits it; the marker is committed exactly once by UnitOfWorkBehavior's single
        /// SaveChangesAsync, which is what makes the marker and the handler's work atomic.
        /// The guarantee holds whenever every reliability extension call names the same
        /// <typeparamref name="TContext"/>, including a lone WithInboxBehavior&lt;TContext&gt;()
        /// call, which registers the matching unit of work itself. IUnitOfWork resolves to the
        /// TContext of the last call to any of WithUnitOfWorkBehavior&lt;TContext&gt;(),
        /// WithInboxBehavior&lt;TContext&gt;(), or WithOutboxProcessingBehavior&lt;TContext&gt;();
        /// IBrokeredMessageInbox resolves to the TContext of the last
        /// WithInboxBehavior&lt;TContext&gt;(). It is void only when a later
        /// WithUnitOfWorkBehavior or WithOutboxProcessingBehavior call names a different
        /// TContext, leaving the unit of work committing a different DbContext than the one
        /// holding the marker. See
        /// docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md.
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

            try
            {
                _logger.LogDebug("Executing message handler from inbox");
                await handler().ConfigureAwait(false);
                _logger.LogDebug("Message handler executed successfully from inbox");

                if (existingMarker is null)
                {
                    var inboxMessage = new InboxMessage()
                    {
                        MessageId = messageId,
                        ReceivedByInboxAtUtc = DateTime.UtcNow
                    };

                    _logger.LogTrace($"Adding inbox message with id '{inboxMessage.MessageId}' and date received '{inboxMessage.ReceivedByInboxAtUtc}'.");
                    await _inbox.AddAsync(inboxMessage, cancellationToken).ConfigureAwait(false);
                    _logger.LogTrace($"Message with id '{messageId}' added to inbox.");
                }
                else
                {
                    existingMarker.ReceivedByInboxAtUtc = DateTime.UtcNow;
                    _logger.LogTrace($"Inbox message with id '{messageId}' refreshed with date received '{existingMarker.ReceivedByInboxAtUtc}'.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace($"Error adding message with id '{messageId}' to inbox: {ex.StackTrace}");
                throw;
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
