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

        public BrokeredMessageInbox(TContext context, ILogger<BrokeredMessageInbox<TContext>> logger, ReliabilityOptions options)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
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
                await handler();
                return;
            }

            _logger.LogTrace($"Checking inbox for brokered message with message id '{messageId}'.");

            if (await _inbox.AnyAsync(m => m.MessageId == messageId))
            {
                _logger.LogTrace($"Message with id '{messageId}' found in inbox. Message will not be handled.");
                return;
            }

            try
            {
                _logger.LogDebug("Executing message handler from inbox");
                await handler();
                var inboxMessage = new InboxMessage()
                {
                    MessageId = messageId,
                    ReceivedByInboxAtUtc = DateTime.UtcNow
                };

                _logger.LogDebug("Message handler executed successfully from inbox");
                _logger.LogTrace($"Adding inbox message with id '{inboxMessage.MessageId}' and date received '{inboxMessage.ReceivedByInboxAtUtc}'.");
                await _inbox.AddAsync(inboxMessage);
                _logger.LogTrace($"Message with id '{messageId}' added to inbox.");
            }
            catch (Exception ex)
            {
                _logger.LogTrace($"Error adding message with id '{messageId}' to inbox: {ex.StackTrace}");
                throw;
            }
        }

        public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
            => _inbox.AnyAsync(m => m.MessageId == messageId, cancellationToken);
    }
}
