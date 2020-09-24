using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public class InMemoryBrokeredMessageInbox : IBrokeredMessageInbox
    {
        private readonly ConcurrentDictionary<string, bool> _inbox;
        private readonly ILogger<InMemoryBrokeredMessageInbox> _logger;

        public InMemoryBrokeredMessageInbox(ILogger<InMemoryBrokeredMessageInbox> logger)
        {
            _inbox = new ConcurrentDictionary<string, bool>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver)
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
