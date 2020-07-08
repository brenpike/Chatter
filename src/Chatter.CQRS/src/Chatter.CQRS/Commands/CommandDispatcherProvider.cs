using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.CQRS.Commands
{
    /// <summary>
    /// Provides an <see cref="IMessageDispatcher"/> for <see cref="ICommand"/>
    /// </summary>
    internal class CommandDispatcherProvider : IMessageDispatcherProvider
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        ///<inheritdoc/>
        public Type DispatchType => typeof(ICommand);

        public CommandDispatcherProvider(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        ///<inheritdoc/>
        public IMessageDispatcher GetDispatcher()
        {
            return new CommandDispatcher(_serviceScopeFactory);
        }
    }
}
