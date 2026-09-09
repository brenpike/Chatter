using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    class InMemoryBrokeredMessageOutbox : IBrokeredMessageOutbox, IPollableOutboxStore, IUnitOfWork
    {
        private readonly ConcurrentDictionary<string, OutboxMessage> _outbox;
        private readonly ILogger<InMemoryBrokeredMessageOutbox> _logger;
        private readonly ReliabilityOptions _reliabilityOptions;

        IPersistanceTransaction IUnitOfWork.CurrentTransaction => null;
        bool IUnitOfWork.HasActiveTransaction => false;

        public InMemoryBrokeredMessageOutbox(ILogger<InMemoryBrokeredMessageOutbox> logger, ReliabilityOptions reliabilityOptions)
        {
            _outbox = new ConcurrentDictionary<string, OutboxMessage>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reliabilityOptions = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));
        }

        public async Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            foreach (var outboundBrokeredMessage in outboundBrokeredMessages)
            {
                await SendToOutbox(outboundBrokeredMessage, transactionContext, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            Guid transactionId = Guid.NewGuid();
            if (transactionContext != null)
            {
                transactionContext.Container.TryGet<IPersistanceTransaction>(out var transaction);
                transactionId = transaction?.TransactionId ?? transactionId;
            }

            var outboxMessage = new OutboxMessage
            {
                MessageId = outboundBrokeredMessage.MessageId,
                MessageContext = System.Text.Json.JsonSerializer.Serialize(outboundBrokeredMessage.MessageContext, ChatterJson.Options),
                Destination = outboundBrokeredMessage.Destination,
                MessageBody = outboundBrokeredMessage.Stringify(),
                MessageContentType = outboundBrokeredMessage.ContentType,
                SentToOutboxAtUtc = DateTime.UtcNow,
                ProcessedFromOutboxAtUtc = null,
                BatchId = transactionId
            };

            _logger.LogTrace($"Outbox message created for message with id: '{outboxMessage.MessageId}'");

            if (!_outbox.TryAdd(outboxMessage.MessageId, outboxMessage))
            {
                var error = $"Unable to add brokered message with id: '{outboxMessage.MessageId}' to the in memory outbox.";
                _logger.LogError(error);
                throw new InvalidOperationException(error);
            }

            _logger.LogTrace($"Outbox message with id: '{outboxMessage.MessageId}' added to the in memory outbox.");

            return Task.CompletedTask;
        }

        public Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default)
                => Task.FromResult<IEnumerable<OutboxMessage>>(_outbox.Values
                        .Where(m => m.ProcessedFromOutboxAtUtc is null)
                        .ToList());

        public Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default)
        {
            foreach (var outboxMessage in outboxMessages)
            {
                outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            }
            RemoveExpiredFromInboxOutbox();
            return Task.CompletedTask;
        }

        public Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            RemoveExpiredFromInboxOutbox();
            return Task.CompletedTask;
        }

        private void RemoveExpiredFromInboxOutbox()
        {
            var ttl = _reliabilityOptions.MinutesToLiveInMemory;

            if (ttl <= 0)
            {
                return;
            }

            foreach (var kvp in _outbox)
            {
                var message = kvp.Value;
                if (!message.ProcessedFromOutboxAtUtc.HasValue)
                {
                    continue;
                }

                // INVARIANT: the ttl is compared against ELAPSED time rather than added to the processed
                // timestamp, so no expiry instant is computed and there is nothing left to overflow -
                // subtracting two DateTime values always fits a TimeSpan and comparing two doubles is total.
                // AddMinutes was the one operation here that could throw, and OutboxProcessor calls
                // UpdateProcessedDate inside its unit of work BEFORE dispatching and catches every exception
                // around the whole block, so a ttl too large to add stamped each message processed, logged one
                // line and left it never dispatched and never retried. A ttl no elapsed time can reach now
                // simply never expires anything, which is what such a ttl asks for.
                if ((DateTime.UtcNow - message.ProcessedFromOutboxAtUtc.Value).TotalMinutes < ttl)
                {
                    continue;
                }

                _outbox.TryRemove(kvp.Key, out _);
            }
        }

        public Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid transactionId, CancellationToken cancellationToken = default)
                => Task.FromResult<IEnumerable<OutboxMessage>>(_outbox.Values
                        .Where(m => m.ProcessedFromOutboxAtUtc is null && m.BatchId == transactionId)
                        .ToList());

        // INVARIANT: a NON-TRANSACTIONAL pass-through. OutboxProcessor obtains the unit of work by
        // casting the single resolved outbox (Reliability-Store Facet Resolution), so the default
        // in-memory store must realize this facet or the drain never stamps a processed date and
        // never dispatches. There is nothing to enlist in-memory, so the operation runs as-is and
        // its failure travels out unchanged rather than being swallowed by a fake transaction.
        Task IUnitOfWork.ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken)
            => operation(cancellationToken);
    }
}
