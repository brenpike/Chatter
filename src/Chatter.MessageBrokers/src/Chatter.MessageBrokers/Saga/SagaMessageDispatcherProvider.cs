using Chatter.CQRS;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.Saga
{
    public sealed class SagaMessageDispatcherProvider : IMessageDispatcherProvider
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public SagaMessageDispatcherProvider(IServiceScopeFactory serviceFactory)
        {
            _serviceFactory = serviceFactory;
        }

        public Type DispatchType => typeof(ISagaMessage);

        public IMessageDispatcher CreateDispatcher<TMessage>() where TMessage : IMessage
        {
            return new SagaMessageDispatcher(_serviceFactory);
        }
    }
}
