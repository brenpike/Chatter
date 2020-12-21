using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.MessageBrokers.SqlServiceBroker.Sending
{
    class SqlServiceBrokerSenderFactory : IMessagingInfrastructureDispatcherFactory
    {
        private readonly IServiceScopeFactory _serviceProvider;

        public SqlServiceBrokerSenderFactory(IServiceScopeFactory serviceProvider)
            => _serviceProvider = serviceProvider;

        public IMessagingInfrastructureDispatcher Create()
        {
            using var sp = _serviceProvider.CreateScope();
            return sp.ServiceProvider.GetRequiredService<SqlServiceBrokerSender>();
        }
    }
}
