using Chatter.MessageBrokers.Context;
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
    class InMemoryBrokeredMessageProcessor : ITransactionalBrokeredMessageOutbox
    {
        private readonly ConcurrentDictionary<string, OutboxMessage> _outbox;
        private readonly ILogger<InMemoryBrokeredMessageProcessor> _logger;
        private readonly ConcurrentDictionary<string, bool> _inbox;

        public InMemoryBrokeredMessageProcessor(ILogger<InMemoryBrokeredMessageProcessor> logger)
        {
            _inbox = new ConcurrentDictionary<string, bool>();
            _outbox = new ConcurrentDictionary<string, OutboxMessage>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            if (_outbox.TryAdd(outboxMessage.MessageId, outboxMessage))
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

        public async Task ProcessFromOutbox(IEnumerable<OutboxMessage> outboxMessages)
        {
            foreach (var outboxMessage in outboxMessages)
            {
                await ProcessFromOutbox(outboxMessage).ConfigureAwait(false);
            }
        }

        public Task ProcessFromOutbox(OutboxMessage outboxMessage)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            //TODO: how do we remove messages? have a TTL? remove after processing?
            return Task.CompletedTask;
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

            //for a database inbox implementation, a database transaction would typically be started her and commited after messageReceiver runs
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
