using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.CQRS.Events
{
    /// <summary>
    /// Provides an <see cref="IMessageDispatcher"/> for <see cref="IEvent"/>
    /// </summary>
    internal class EventDispatcherProvider : IMessageDispatcherProvider
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        ///<inheritdoc/>
        public Type DispatchType => typeof(IEvent);

        public EventDispatcherProvider(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        ///<inheritdoc/>
        public IMessageDispatcher GetDispatcher()
        {
            return new EventDispatcher(_serviceScopeFactory);
        }
    }
}
