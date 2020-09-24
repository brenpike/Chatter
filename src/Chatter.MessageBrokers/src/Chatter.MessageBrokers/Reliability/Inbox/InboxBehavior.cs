using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public class InboxBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : IMessage
    {
        private readonly IBrokeredMessageInbox _brokeredMessageInbox;

        public InboxBehavior(IBrokeredMessageInbox brokeredMessageInbox)
        {
            _brokeredMessageInbox = brokeredMessageInbox ?? throw new ArgumentNullException(nameof(brokeredMessageInbox));
        }

        public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
            {
                return _brokeredMessageInbox.ReceiveViaInbox(message, messageBrokerContext, () => next());
            }

            return next();
        }
    }
}
