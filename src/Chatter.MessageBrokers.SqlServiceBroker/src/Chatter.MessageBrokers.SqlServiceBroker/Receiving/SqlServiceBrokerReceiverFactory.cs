using Chatter.MessageBrokers.Receiving;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    class SqlServiceBrokerReceiverFactory : IMessagingInfrastructureReceiverFactory
    {
        private readonly IServiceScopeFactory _serviceProvider;

        public SqlServiceBrokerReceiverFactory(IServiceScopeFactory serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IMessagingInfrastructureReceiver Create()
        {
            using var sp = _serviceProvider.CreateScope();
            return sp.ServiceProvider.GetRequiredService<SqlServiceBrokerReceiver>();
        }
    }
}
