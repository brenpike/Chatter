using Chatter.CQRS.Context;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace Chatter.CQRS.Events
{
    internal sealed class EventDispatcher : IMessageDispatcher
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public EventDispatcher(IServiceScopeFactory serviceFactory)
        {
            _serviceFactory = serviceFactory;
        }

        public async Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage
        {
            await Dispatch(message, new MessageHandlerContext()).ConfigureAwait(false);
        }

        public async Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            using var scope = _serviceFactory.CreateScope();
            var handlers = scope.ServiceProvider.GetServices<IMessageHandler<TMessage>>();
            foreach (var handler in handlers)
            {
                await handler.Handle(message, new MessageHandlerContext()).ConfigureAwait(false);
            }
        }
    }
}
