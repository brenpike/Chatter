using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    public class TransactionalOutboxMessageDispatcherDecorator : IMessageDispatcher
    {
        private readonly IMessageDispatcher _messageDispatcher;
        private readonly ITransactionalBrokeredMessageOutbox _reliableBrokeredMessageProcessor;

        public TransactionalOutboxMessageDispatcherDecorator(IMessageDispatcher messageDispatcher, ITransactionalBrokeredMessageOutbox reliableBrokeredMessageProcessor)
        {
            _messageDispatcher = messageDispatcher ?? throw new ArgumentNullException(nameof(messageDispatcher));
            _reliableBrokeredMessageProcessor = reliableBrokeredMessageProcessor ?? throw new ArgumentNullException(nameof(reliableBrokeredMessageProcessor));
        }

        public Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage
        {
            return Dispatch(message, new MessageHandlerContext());
        }

        public Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
            {
                _reliableBrokeredMessageProcessor.Receive(message, messageBrokerContext, () => _messageDispatcher.Dispatch(message, messageHandlerContext));
            }

            return _messageDispatcher.Dispatch(message, messageHandlerContext);
        }
    }
}
