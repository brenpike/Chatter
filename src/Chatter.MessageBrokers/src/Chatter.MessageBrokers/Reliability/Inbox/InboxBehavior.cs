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
        private readonly ITransactionalBrokeredMessageOutbox _transactionalBrokeredMessageOutbox;

        public InboxBehavior(ITransactionalBrokeredMessageOutbox transactionalBrokeredMessageOutbox)
        {
            _transactionalBrokeredMessageOutbox = transactionalBrokeredMessageOutbox ?? throw new ArgumentNullException(nameof(transactionalBrokeredMessageOutbox));
        }

        public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
            {
                return _transactionalBrokeredMessageOutbox.ReceiveViaInbox(message, messageBrokerContext, () => next());
            }

            return next();
        }
    }
}
