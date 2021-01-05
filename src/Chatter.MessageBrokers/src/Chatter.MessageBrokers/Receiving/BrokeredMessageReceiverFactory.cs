using Chatter.CQRS;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.Receiving
{
    class BrokeredMessageReceiverFactory : IBrokeredMessageReceiverFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public BrokeredMessageReceiverFactory(IServiceProvider serviceProvider) 
            => _serviceProvider = serviceProvider;

        public IBrokeredMessageReceiver<TMessage> Create<TMessage>() where TMessage : class, IMessage 
            => _serviceProvider.GetRequiredService<IBrokeredMessageReceiver<TMessage>>();
    }
}
