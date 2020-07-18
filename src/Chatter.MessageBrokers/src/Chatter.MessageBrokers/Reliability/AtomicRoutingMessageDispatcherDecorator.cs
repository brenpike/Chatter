using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    public class AtomicRoutingMessageDispatcherDecorator : IMessageDispatcher
    {
        private readonly IMessageDispatcher _messageDispatcher;

        public AtomicRoutingMessageDispatcherDecorator(IMessageDispatcher messageDispatcher)
        {
            _messageDispatcher = messageDispatcher ?? throw new ArgumentNullException(nameof(messageDispatcher));
        }

        public Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage
        {
            return Dispatch(message, new MessageHandlerContext());
        }

        public async Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            try
            {
                await _messageDispatcher.Dispatch(message, messageHandlerContext).ConfigureAwait(false);

                if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
                {
                    var inboundMessage = messageBrokerContext.BrokeredMessage;
                    messageBrokerContext.Container.TryGet<TransactionContext>(out var transactionContext);
                    await messageBrokerContext.NextDestinationRouter.Route(inboundMessage, transactionContext, messageBrokerContext.GetNextDestinationContext()).ConfigureAwait(false);
                    await messageBrokerContext.ReplyRouter.Route(inboundMessage, transactionContext, messageBrokerContext.GetReplyContext()).ConfigureAwait(false);
                }
            }
            catch (Exception dispatchFailureException)
            {
                if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
                {
                    messageBrokerContext.Container.TryGet<CompensateContext>(out var compensateContext);
                    messageBrokerContext.Container.TryGet<TransactionContext>(out var transactionContext);

                    var details = $"{dispatchFailureException.Message} -> {dispatchFailureException.StackTrace}";
                    var description = $"'{typeof(TMessage).Name}' was not received successfully";

                    var newContext = new CompensateContext(compensateContext?.DestinationPath,
                                                           compensateContext?.DestinationMessageCreator,
                                                           details,
                                                           description,
                                                           messageBrokerContext?.Container);

                    await messageBrokerContext.CompensateRouter.Route(messageBrokerContext?.BrokeredMessage,
                                                                      transactionContext,
                                                                      newContext);
                }
                else
                {
                    throw;
                }
            }
        }
    }
}
