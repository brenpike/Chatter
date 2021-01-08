using Chatter.MessageBrokers.Receiving;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    class ServiceBusReceiverFactory : IMessagingInfrastructureReceiverFactory
    {
        private readonly IServiceScopeFactory _serviceProvider;

        public ServiceBusReceiverFactory(IServiceScopeFactory serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IMessagingInfrastructureReceiver Create()
        {
            using var sp = _serviceProvider.CreateScope();
            return sp.ServiceProvider.GetRequiredService<ServiceBusReceiver>();
        }
    }
}
