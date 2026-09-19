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

        IPersistanceTransaction IUnitOfWork.CurrentTransaction => _unitOfWork.CurrentTransaction;
        bool IUnitOfWork.HasActiveTransaction => _unitOfWork.HasActiveTransaction;

        /// <summary>
        /// Creates an outbox whose poll takes every unprocessed message, however many there are.
        /// </summary>
        /// <remarks>
        /// This is the legacy uncapped path, kept for a caller that constructs the outbox itself. The container
        /// resolves the overload taking <see cref="ReliabilityOptions"/>, so a Chatter-configured host polls the
        /// Outbox Poll Batch rather than the whole backlog.
        /// </remarks>
        public BrokeredMessageOutbox(TContext context, ILoggerFactory loggerFactory)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _ = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            _logger = loggerFactory.CreateLogger<BrokeredMessageOutbox<TContext>>();
            _unitOfWork = new UnitOfWork<TContext>(context, loggerFactory.CreateLogger<UnitOfWork<TContext>>());
            _outboxPollBatchSize = null;
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
        }

        /// <remarks>
        /// INVARIANT: this query stays TRACKED. The claim staged by <see cref="UpdateProcessedDate(OutboxMessage, CancellationToken)"/>
        /// is safe under concurrency only because a tracked message keeps the ProcessedFromOutboxAtUtc it was loaded
        /// with as its original value, which OutboxMessageConfiguration maps to a concurrency token and EF therefore
        /// emits as a 'still unprocessed' predicate on the claiming update. Reading this poll with AsNoTracking would
        /// hand back a detached message whose original value IS the stamp being written, so the claim would match no
        /// row and every drain would fail. Proven over a real database by
        /// Integration/WhenClaimingOutboxConcurrentlyOnSqlServer.
        /// </remarks>
        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default)
        {
            var outbox = _context.Set<OutboxMessage>();
            var unprocessed = outbox.Where(message => message.ProcessedFromOutboxAtUtc == null)
                                    .OrderBy(message => message.SentToOutboxAtUtc);

            if (_outboxPollBatchSize is null)
            {
                return await unprocessed.ToListAsync(cancellationToken).ConfigureAwait(false);
            }

            return await unprocessed.Take(_outboxPollBatchSize.Value).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

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
