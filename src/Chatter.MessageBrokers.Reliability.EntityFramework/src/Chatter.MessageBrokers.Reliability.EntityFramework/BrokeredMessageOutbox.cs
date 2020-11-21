using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    public class BrokeredMessageOutbox<TContext> : IBrokeredMessageOutbox where TContext : DbContext
    {
        private readonly TContext _context;
        private readonly ILogger<BrokeredMessageOutbox<TContext>> _logger;
        private readonly ReliabilityOptions _options;

        public BrokeredMessageOutbox(TContext context, ILogger<BrokeredMessageOutbox<TContext>> logger, ReliabilityOptions options)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox()
        {
            var outbox = _context.Set<OutboxMessage>();
            return await outbox.Where(message => message.ProcessedFromOutboxAtUtc == null).ToListAsync();
        }

        public Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages)
        {
            var set = _context.Set<OutboxMessage>();
            foreach (var message in outboxMessages)
            {
                UpdateProcessedDate(set, message);
            }

            return _context.SaveChangesAsync();
        }

        public Task UpdateProcessedDate(OutboxMessage outboxMessage)
        {
            UpdateProcessedDate(_context.Set<OutboxMessage>(), outboxMessage);
            return _context.SaveChangesAsync();
        }

        private void UpdateProcessedDate(DbSet<OutboxMessage> outbox, OutboxMessage outboxMessage)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            outbox.Update(outboxMessage);
        }

        public async Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            var outbox = _context.Set<OutboxMessage>();

            await SendToOutbox(outbox, outboundBrokeredMessage, transactionContext);
            await _context.SaveChangesAsync();

            _logger.LogTrace($"Outbox message added to outbox. Id: '{outboundBrokeredMessage.MessageId}'");
        }

        public async Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
        {
            var outbox = _context.Set<OutboxMessage>();

            foreach (var obm in outboundBrokeredMessages)
            {
                await SendToOutbox(outbox, obm, transactionContext);
            }

            var numMessagesSavedToOutbox = await _context.SaveChangesAsync();

            _logger.LogTrace($"'{numMessagesSavedToOutbox}' outbox message(s) added to outbox.");
        }

        private async Task SendToOutbox(DbSet<OutboxMessage> outbox, OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            var currentTransaction = transactionContext?.Container.GetOrDefault<IPersistanceTransaction>();
            Guid transactionId = currentTransaction?.TransactionId ?? Guid.Empty;

            var outboxMessage = new OutboxMessage
            {
                MessageId = outboundBrokeredMessage.MessageId,
                MessageContext = JsonConvert.SerializeObject(outboundBrokeredMessage.MessageContext),
                Destination = outboundBrokeredMessage.Destination,
                MessageBody = outboundBrokeredMessage.Stringify(),
                MessageContentType = outboundBrokeredMessage.GetContentType(),
                SentToOutboxAtUtc = DateTime.UtcNow,
                ProcessedFromOutboxAtUtc = null,
                BatchId = transactionId
            };

            _logger.LogTrace($"Outbox message created for message with id: '{outboxMessage.MessageId}'");

            await outbox.AddAsync(outboxMessage);
        }

        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid batchId)
        {
            var outbox = _context.Set<OutboxMessage>();
            return await outbox.Where(message => message.ProcessedFromOutboxAtUtc == null && message.BatchId == batchId).ToListAsync();
        }
    }
}
