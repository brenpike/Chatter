using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    /// <summary>
    /// An inbox which keeps track of brokered messages which have been processed.
    /// </summary>
    /// <typeparam name="TContext">The DbContext where the inbox presides</typeparam>
    public class BrokeredMessageInbox<TContext> : IBrokeredMessageInbox where TContext : DbContext
    {
        private readonly TContext _context;
        private readonly ILogger<BrokeredMessageInbox<TContext>> _logger;
        private readonly ReliabilityOptions _options;

        public BrokeredMessageInbox(TContext context, ILogger<BrokeredMessageInbox<TContext>> logger, ReliabilityOptions options)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Receives a message and verifies if it's been handled previously by checking the inbox
        /// </summary>
        /// <typeparam name="TMessage">The type of message being received</typeparam>
        /// <param name="message">The message being received</param>
        /// <param name="messageBrokerContext">The brokered message context received with the message</param>
        /// <param name="handler">The message handler to be executed if the message is not found within the inbox</param>
        /// <returns>An awaitable task</returns>
        public async Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> handler)
        {
            var messageId = messageBrokerContext?.BrokeredMessage?.MessageId;

            _logger.LogTrace($"Checking inbox for brokered message with message id '{messageId}'.");

            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new ArgumentException("Message id to be processed cannot be empty.", nameof(messageId));
            }

            var inbox = _context.Set<InboxMessage>();

            if (await inbox.AnyAsync(m => m.MessageId == messageId))
            {
                _logger.LogTrace($"Message with id '{messageId}' found in inbox. Message will not be handled.");
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

                _logger.LogTrace($"Handler executed successfully. Adding inbox message with id '{inboxMessage.MessageId}' and date received '{inboxMessage.ReceivedByInboxAtUtc}'.");

                await inbox.AddAsync(inboxMessage);
                _logger.LogTrace($"Message with id '{messageId}' added to inbox.");
            }
            catch (Exception ex)
            {
                _logger.LogTrace($"Error adding message with id '{messageId}' to inbox: {ex.StackTrace}");
                throw;
            }
        }
    }
}
