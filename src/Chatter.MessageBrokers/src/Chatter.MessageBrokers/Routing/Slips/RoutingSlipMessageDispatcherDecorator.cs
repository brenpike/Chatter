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

        public RoutingSlipMessageDispatcherDecorator(IMessageDispatcher messageDispatcher)
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
                    await messageBrokerContext.ReplyTo().ConfigureAwait(false);
                    await messageBrokerContext.Forward<RoutingContext>().ConfigureAwait(false);
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

                    await messageBrokerContext.Compensate();
                }
                else
                {
                    throw;
                }
            }
        }
    }
}
