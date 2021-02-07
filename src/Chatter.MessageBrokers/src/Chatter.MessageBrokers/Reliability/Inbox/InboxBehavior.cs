using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public class InboxBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
    {
        private readonly IBrokeredMessageInbox _brokeredMessageInbox;
        private readonly ILogger<InboxBehavior<TMessage>> _logger;

        public InboxBehavior(IBrokeredMessageInbox brokeredMessageInbox, ILogger<InboxBehavior<TMessage>> logger)
        {
            _brokeredMessageInbox = brokeredMessageInbox ?? throw new ArgumentNullException(nameof(brokeredMessageInbox));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            _logger.LogDebug($"Entering {nameof(InboxBehavior<TMessage>)}");
            if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
            {
                return _brokeredMessageInbox.ReceiveViaInbox(message, messageBrokerContext, () => next());
            }

            return next();
        }
    }
}
