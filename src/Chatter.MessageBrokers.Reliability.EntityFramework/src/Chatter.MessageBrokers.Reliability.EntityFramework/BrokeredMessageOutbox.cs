using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    public class BrokeredMessageOutbox<TContext> : IBrokeredMessageOutbox where TContext : DbContext
    {
        private readonly TContext _context;
        private readonly ILogger<BrokeredMessageOutbox<TContext>> _logger;

        public BrokeredMessageOutbox(TContext context, ILogger<BrokeredMessageOutbox<TContext>> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default)
        {
            var outbox = _context.Set<OutboxMessage>();
            return await outbox.Where(message => message.ProcessedFromOutboxAtUtc == null).ToListAsync(cancellationToken);
        }

        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId, CancellationToken cancellationToken = default)
        {
            var outbox = _context.Set<OutboxMessage>();
            return await outbox.Where(message => message.ProcessedFromOutboxAtUtc == null && message.BatchId == batchId).ToListAsync(cancellationToken);
        }

        public Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default)
        {
            var set = _context.Set<OutboxMessage>();
            foreach (var message in outboxMessages)
            {
                UpdateProcessedDate(set, message);
            }

            return SaveOutboxAsync(cancellationToken);
        }

        public Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default)
        {
            UpdateProcessedDate(_context.Set<OutboxMessage>(), outboxMessage);
            return SaveOutboxAsync(cancellationToken);
        }

        private void UpdateProcessedDate(DbSet<OutboxMessage> outbox, OutboxMessage outboxMessage)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            outbox.Update(outboxMessage);
        }

        public Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext, CancellationToken cancellationToken = default)
            => SendToOutbox(new[] { outboundBrokeredMessage }, transactionContext, cancellationToken);

        public async Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            var outbox = _context.Set<OutboxMessage>();

            foreach (var obm in outboundBrokeredMessages)
            {
                await SendToOutboxImpl(outbox, obm, transactionContext, cancellationToken).ConfigureAwait(false);
            }

            var numMessagesSavedToOutbox = await SaveOutboxAsync(cancellationToken);

            _logger.LogTrace($"{numMessagesSavedToOutbox} outbox message(s) saved to outbox.");
        }

        public async Task<int> SaveOutboxAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ce)
            {
                foreach (var entry in ce.Entries)
                {
                    if (entry.Entity is OutboxMessage)
                    {
                        var dbVal = await entry.GetDatabaseValuesAsync(cancellationToken);
                        var processedTime = dbVal[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)];
                        _logger.LogWarning(ce, $"Outbox message was already processed at {processedTime}");
                    }
                }

                return 0;
            }
        }

        private async Task SendToOutboxImpl(DbSet<OutboxMessage> outbox, OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            var currentTransaction = transactionContext?.Container.GetOrDefault<IPersistanceTransaction>();
            Guid transactionId = currentTransaction?.TransactionId ?? Guid.Empty;

            var outboxMessage = new OutboxMessage
            {
                MessageId = outboundBrokeredMessage.MessageId,
                MessageContext = JsonConvert.SerializeObject(outboundBrokeredMessage.MessageContext),
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
    }
}
