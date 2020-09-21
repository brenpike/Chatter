using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    public class TransactionalOutbox<TContext> : ITransactionalBrokeredMessageOutbox where TContext : DbContext
    {
        private readonly TContext _context;
        private readonly ILogger<TransactionalOutbox<TContext>> _logger;
        private readonly ReliabilityOptions _options;

        public TransactionalOutbox(TContext context, ILogger<TransactionalOutbox<TContext>> logger, ReliabilityOptions options)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<IEnumerable<OutboxMessage>> GetUnprocessedBrokeredMessagesFromOutbox()
        {
            var outbox = _context.Set<OutboxMessage>();
            return await outbox.Where(message => message.ProcessedFromOutboxAtUtc == null).ToListAsync();
        }

        public Task MarkMessageAsProcessed(IEnumerable<OutboxMessage> outboxMessages)
        {
            var set = _context.Set<OutboxMessage>();
            foreach (var message in outboxMessages)
            {
                MarkMessageAsProcessed(set, message);
            }

            return _context.SaveChangesAsync();
        }

        public Task MarkMessageAsProcessed(OutboxMessage outboxMessage)
        {
            MarkMessageAsProcessed(_context.Set<OutboxMessage>(), outboxMessage);
            return _context.SaveChangesAsync();
        }

        private void MarkMessageAsProcessed(DbSet<OutboxMessage> outbox, OutboxMessage outboxMessage)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            outbox.Update(outboxMessage);
        }

        public async Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> handler)
        {
            var messageId = messageBrokerContext.BrokeredMessage.MessageId;

            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new ArgumentException("Message id to be processed cannot be empty.", nameof(messageId));
            }

            var inbox = _context.Set<InboxMessage>();

            if (await inbox.AnyAsync(m => m.MessageId == messageId))
            {
                return;
            }

            try
            {
                await handler();

                var inboxMessage = new InboxMessage()
                {
                    MessageId = messageId,
                    ReceivedByInboxAtUtc = DateTime.UtcNow
                };

                await inbox.AddAsync(inboxMessage);
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        public async Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            var outbox = _context.Set<OutboxMessage>();

            await SendToOutbox(outbox, outboundBrokeredMessage, transactionContext);
            await _context.SaveChangesAsync();

            _logger.LogTrace($"Outbox message added to outbox. Id: '{outboundBrokeredMessage.MessageId}'");
        }

        public async Task SendToOutbox(IList<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
        {
            var outbox = _context.Set<OutboxMessage>();

            foreach (var obm in outboundBrokeredMessages)
            {
                await SendToOutbox(outbox, obm, transactionContext);
            }

            var numMessagesSavedToOutbox = await _context.SaveChangesAsync();

            _logger.LogTrace($"{numMessagesSavedToOutbox} outbox message added to outbox.");
        }

        private async Task SendToOutbox(DbSet<OutboxMessage> outbox, OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            var outboxMessage = new OutboxMessage
            {
                MessageId = outboundBrokeredMessage.MessageId,
                ApplicationProperties = outboundBrokeredMessage.ApplicationProperties,
                Body = outboundBrokeredMessage.Body,
                Destination = outboundBrokeredMessage.Destination,
                StringifiedMessage = outboundBrokeredMessage.Stringify(),
                SentToOutboxAtUtc = DateTime.UtcNow,
                ProcessedFromOutboxAtUtc = null
            };

            _logger.LogTrace($"Outbox message created for message with id: '{outboxMessage.MessageId}'");

            await outbox.AddAsync(outboxMessage);
        }
    }
}
