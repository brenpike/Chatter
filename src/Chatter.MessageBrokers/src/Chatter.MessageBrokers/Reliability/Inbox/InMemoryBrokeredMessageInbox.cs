using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public class InMemoryBrokeredMessageInbox : IBrokeredMessageInbox, IInboxDeduplicator, IProcessLifetimeStore
    {
        // INVARIANT: this dictionary is the process's ONLY record of which message ids have been received, so the
        // instance holding it must outlive any DI scope. ScopedReceivedMessageDispatcher opens a FRESH scope per
        // delivery, so a per-scope instance starts every redelivery with an empty dictionary and deduplicates
        // nothing. Hence IProcessLifetimeStore and the process-lifetime registration; the relational and document
        // provider stores keep their markers outside the instance and are correctly per-operation.
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

            // INVARIANT: the id is reserved before the receiver runs, so concurrent receipts of one
            // message id contend on the reservation and only the winner invokes the receiver.
            if (!_inbox.TryAdd(id, true))
            {
                _logger.LogTrace($"Brokered message of type '{typeof(TMessage).Name}' with id: '{id}' was already received.");
                return;
            }

            try
            {
                await messageReceiver().ConfigureAwait(false);
            }
            catch
            {
                _inbox.TryRemove(id, out _);
                throw;
            }

            _logger.LogTrace($"Brokered message of type '{typeof(TMessage).Name}' with id: '{id}' was successfully received and added to inbox.");
        }

        public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(_inbox.ContainsKey(messageId));
    }
}
