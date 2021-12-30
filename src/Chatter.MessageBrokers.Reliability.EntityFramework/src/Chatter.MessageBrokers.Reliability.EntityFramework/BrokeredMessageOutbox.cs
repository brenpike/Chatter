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
    public class BrokeredMessageOutbox<TContext> : IBrokeredMessageOutbox, IUnitOfWork where TContext : DbContext
    {
        private readonly TContext _context;
        private readonly ILogger<BrokeredMessageOutbox<TContext>> _logger;
        private readonly UnitOfWork<TContext> _unitOfWork;

        IPersistanceTransaction IUnitOfWork.CurrentTransaction => _unitOfWork.CurrentTransaction;
        bool IUnitOfWork.HasActiveTransaction => _unitOfWork.HasActiveTransaction;

        public BrokeredMessageOutbox(TContext context, ILoggerFactory loggerFactory)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _ = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            _logger = loggerFactory.CreateLogger<BrokeredMessageOutbox<TContext>>();
            _unitOfWork = new UnitOfWork<TContext>(context, loggerFactory.CreateLogger<UnitOfWork<TContext>>());
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

        public async Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            var outbox = _context.Set<OutboxMessage>();

            foreach (var obm in outboundBrokeredMessages)
            {
                await SendToOutboxImpl(outbox, obm, transactionContext, cancellationToken).ConfigureAwait(false);
            }

            await SaveOutboxAsync(cancellationToken);
        }

        public async Task<int> SaveOutboxAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var rowCnt = await _context.SaveChangesAsync(cancellationToken);
                _logger.LogTrace($"'{rowCnt}' outbox message(s) saved.");
                return rowCnt;
            }
            catch (DbUpdateConcurrencyException ce)
            {
                foreach (var entry in ce.Entries)
                {
                    if (entry.Entity is OutboxMessage)
                    {
                        var dbVal = await entry.GetDatabaseValuesAsync(cancellationToken);
                        var processedTime = dbVal[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)];
                        var messageId = dbVal[nameof(OutboxMessage.Id)];

                        _logger.LogWarning(ce, $"Outbox message with id '{messageId}' was already processed at '{processedTime}'");

                        entry.OriginalValues.SetValues(dbVal);
                        entry.State = EntityState.Unchanged;
                    }
                }

                throw;
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

        Task IUnitOfWork.ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken)
            => _unitOfWork.ExecuteAsync(cf => operation(cf), transactionContext, cancellationToken);
    }
}
