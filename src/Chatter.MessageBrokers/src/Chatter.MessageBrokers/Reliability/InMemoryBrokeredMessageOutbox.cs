using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    class InMemoryBrokeredMessageOutbox : ITransactionalBrokeredMessageOutbox
    {
        private readonly ConcurrentDictionary<string, OutboxMessage> _outbox;
        private readonly ILogger<InMemoryBrokeredMessageOutbox> _logger;
        private readonly ReliabilityOptions _reliabilityOptions;
        private readonly ConcurrentDictionary<string, bool> _inbox;

        public InMemoryBrokeredMessageOutbox(ILogger<InMemoryBrokeredMessageOutbox> logger, ReliabilityOptions reliabilityOptions)
        {
            _inbox = new ConcurrentDictionary<string, bool>();
            _outbox = new ConcurrentDictionary<string, OutboxMessage>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reliabilityOptions = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));
        }

        public async Task SendToOutbox(IList<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
        {
            foreach (var outboundBrokeredMessage in outboundBrokeredMessages)
            {
                await SendToOutbox(outboundBrokeredMessage, transactionContext).ConfigureAwait(false);
            }
        }

        public Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
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

            if (!_outbox.TryAdd(outboxMessage.MessageId, outboxMessage))
            {
                var error = $"Unable to add brokered message with id: '{outboxMessage.MessageId}' to the in memory outbox.";
                _logger.LogError(error);
                throw new InvalidOperationException(error);
            }

            _logger.LogTrace($"Outbox message with id: '{outboxMessage.MessageId}' added to the in memory outbox.");

            return Task.CompletedTask;
        }

        public Task<IEnumerable<OutboxMessage>> GetUnprocessedBrokeredMessagesFromOutbox()
                => Task.FromResult<IEnumerable<OutboxMessage>>(_outbox.Values
                        .Where(m => m.ProcessedFromOutboxAtUtc is null)
                        .ToList());

        public Task MarkMessageAsProcessed(IEnumerable<OutboxMessage> outboxMessages)
        {
            foreach (var outboxMessage in outboxMessages)
            {
                outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            }
            RemoveExpiredFromInboxOutbox();
            return Task.CompletedTask;
        }

        public Task MarkMessageAsProcessed(OutboxMessage outboxMessage)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            RemoveExpiredFromInboxOutbox();
            return Task.CompletedTask;
        }

        private void RemoveExpiredFromInboxOutbox()
        {
            var ttl = _reliabilityOptions.TimeToLiveInMinutes;

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
                _inbox.TryRemove(message.MessageId, out _);
            }
        }

        public async Task Receive<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver)
        {
            var id = messageBrokerContext.BrokeredMessage.MessageId;

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A brokered message must have a message id to be persisted in the inbox.", nameof(id));
            }

            if (_inbox.ContainsKey(id))
            {
                _logger.LogTrace($"Brokered message of type '{typeof(TMessage).Name}' with id: '{id}' was already received.");
                return;
            }

            //for a database inbox implementation, a database transaction would typically be started here and committed after messageReceiver runs
            //successfully AND the message is added to the inbox. This works because the aggregate would typically be saved as part of the messageReceiver
            //logic, domain events or commands would be routed (and thus handled by the outbox), which means they will all be part of the same transaction

            await messageReceiver().ConfigureAwait(false);

            if (!_inbox.TryAdd(id, true))
            {
                var error = $"Unable to retrieve brokered message of type '{typeof(TMessage).Name}' with id: '{id}' from the in memory inbox.";
                _logger.LogError(error);
                throw new InvalidOperationException(error);
            }

            _logger.LogTrace($"Brokered message of type '{typeof(TMessage).Name}' with id: '{id}' was successfully received and added to inbox.");
        }
    }
}
