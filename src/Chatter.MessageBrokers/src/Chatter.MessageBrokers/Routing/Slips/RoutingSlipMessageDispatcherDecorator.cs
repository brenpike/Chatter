using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlipMessageDispatcherDecorator : IMessageDispatcher
    {
        private readonly IMessageDispatcher _messageDispatcher;
        private readonly IForwardMessages _forwardMessages;
        private readonly IRouteCompensationMessages _compensatingRouter;
        private readonly IReplyRouter _replyRouter;

        public RoutingSlipMessageDispatcherDecorator(IMessageDispatcher messageDispatcher,
                                                     IForwardMessages forwardMessages,
                                                     IRouteCompensationMessages compensatingRouter,
                                                     IReplyRouter replyRouter)
        {
            _messageDispatcher = messageDispatcher ?? throw new ArgumentNullException(nameof(messageDispatcher));
            _forwardMessages = forwardMessages ?? throw new ArgumentNullException(nameof(forwardMessages));
            _compensatingRouter = compensatingRouter;
            _replyRouter = replyRouter ?? throw new ArgumentNullException(nameof(replyRouter));
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
                    messageBrokerContext.Container.TryGet<ReplyToRoutingContext>(out var replyContext);
                    await _replyRouter.Route(messageBrokerContext.BrokeredMessage, messageBrokerContext.GetTransactionContext(), replyContext).ConfigureAwait(false);

                    messageBrokerContext.Container.TryGet<RoutingContext>(out var forwardContext);
                    await _forwardMessages.Route(messageBrokerContext.BrokeredMessage, forwardContext?.DestinationPath, messageBrokerContext.GetTransactionContext()).ConfigureAwait(false);
                }
            }
            catch (Exception dispatchFailureException)
            {
                if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
                {
                    if (!(messageBrokerContext.Container.TryGet<CompensationRoutingContext>(out var compensateContext)))
                    {
                        throw;
                    }

                    var details = $"{dispatchFailureException.Message} -> {dispatchFailureException.StackTrace}";
                    var description = $"'{typeof(TMessage).Name}' was not received successfully";

                    var newContext = new CompensationRoutingContext(compensateContext?.DestinationPath,
                                                           details,
                                                           description,
                                                           messageBrokerContext?.Container);

                    messageBrokerContext?.Container.Include(newContext);

                    messageBrokerContext.Container.TryGet<CompensationRoutingContext>(out var compensationRoutingContext);
                    await _compensatingRouter.Route(messageBrokerContext.BrokeredMessage, messageBrokerContext.GetTransactionContext(), compensationRoutingContext).ConfigureAwait(false);
                }
                else
                {
                    throw;
                }
            }
        }
    }
}
