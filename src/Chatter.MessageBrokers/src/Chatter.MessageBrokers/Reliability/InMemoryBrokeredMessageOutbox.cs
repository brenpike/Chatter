﻿using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    class InMemoryBrokeredMessageOutbox : IBrokeredMessageOutbox
    {
        private readonly ConcurrentDictionary<string, OutboxMessage> _outbox;
        private readonly ILogger<InMemoryBrokeredMessageOutbox> _logger;
        private readonly ReliabilityOptions _reliabilityOptions;

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
                MessageContext = JsonConvert.SerializeObject(outboundBrokeredMessage.MessageContext),
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

            foreach (var (id, message) in _outbox)
            {
                if (!message.ProcessedFromOutboxAtUtc.HasValue)
                {
                    continue;
                }

                if (message.ProcessedFromOutboxAtUtc.Value.AddMinutes(ttl) > DateTime.UtcNow)
                {
                    continue;
                }

                _outbox.TryRemove(id, out _);
            }
        }

        public Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid transactionId, CancellationToken cancellationToken = default)
                => Task.FromResult<IEnumerable<OutboxMessage>>(_outbox.Values
                        .Where(m => m.ProcessedFromOutboxAtUtc is null && m.BatchId == transactionId)
                        .ToList());
    }
}
