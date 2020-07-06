using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.CQRS.Commands
{
    internal class CommandDispatcherProvider : IMessageDispatcherProvider
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public Type DispatchType => typeof(ICommand);

        public CommandDispatcherProvider(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public IMessageDispatcher CreateDispatcher<TMessage>() where TMessage : IMessage
        {
            return new CommandDispatcher(_serviceScopeFactory);
        }
    }
}
