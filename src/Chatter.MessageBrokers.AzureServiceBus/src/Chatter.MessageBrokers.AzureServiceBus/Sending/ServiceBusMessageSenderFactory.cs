using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    class ServiceBusMessageSenderFactory : IMessagingInfrastructureDispatcherFactory
    {
        private readonly IServiceScopeFactory _serviceProvider;

        public ServiceBusMessageSenderFactory(IServiceScopeFactory serviceProvider) 
            => _serviceProvider = serviceProvider;

        public IMessagingInfrastructureDispatcher Create()
        {
            using var sp = _serviceProvider.CreateScope();
            return sp.ServiceProvider.GetRequiredService<ServiceBusMessageSender>();
        }
    }
}
