using Chatter.CQRS.Context;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace Chatter.CQRS.Commands
{
    internal sealed class CommandDispatcher : IMessageDispatcher
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public CommandDispatcher(IServiceScopeFactory serviceFactory)
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
            var handler = scope.ServiceProvider.GetRequiredService<IMessageHandler<TMessage>>();
            await handler.Handle(message, messageHandlerContext).ConfigureAwait(false);
        }
    }
}
