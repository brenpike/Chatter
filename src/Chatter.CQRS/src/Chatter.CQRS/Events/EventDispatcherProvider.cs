using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.CQRS.Events
{
    internal class EventDispatcherProvider : IMessageDispatcherProvider
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public Type DispatchType => typeof(IEvent);

        public EventDispatcherProvider(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public IMessageDispatcher CreateDispatcher<TMessage>() where TMessage : IMessage
        {
            return new EventDispatcher(_serviceScopeFactory);
        }
    }
}
