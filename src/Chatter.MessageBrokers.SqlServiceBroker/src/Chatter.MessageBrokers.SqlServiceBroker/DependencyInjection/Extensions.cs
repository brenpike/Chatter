using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        public static SqlServiceBrokerOptionsBuilder AddSqlServiceBrokerOptions(this IServiceCollection services)
            => new SqlServiceBrokerOptionsBuilder(services);

        public static IChatterBuilder AddSqlServiceBroker(this IChatterBuilder chatterBuilder, Action<SqlServiceBrokerOptionsBuilder> optionsBuilder = null)
        {
            var optBuilder = chatterBuilder.Services.AddSqlServiceBrokerOptions();
            optionsBuilder?.Invoke(optBuilder);
            var options = optBuilder.Build();

            chatterBuilder.Services.AddScoped<SqlServiceBrokerReceiver>();
            chatterBuilder.Services.AddSingleton<IMessagingInfrastructureDispatcherFactory, SqlServiceBrokerSenderFactory>();

            chatterBuilder.Services.AddScoped<SqlServiceBrokerSender>();
            chatterBuilder.Services.AddSingleton<IMessagingInfrastructureReceiverFactory, SqlServiceBrokerReceiverFactory>();

            chatterBuilder.Services.AddScoped<IMessagingInfrastructure>(sp =>
            {
                var sender = sp.GetRequiredService<IMessagingInfrastructureDispatcherFactory>();
                var receiver = sp.GetRequiredService<IMessagingInfrastructureReceiverFactory>();
                return new MessagingInfrastructure(SSBMessageContext.InfrastructureType, receiver, sender);
            });

            chatterBuilder.Services.AddScoped<IBrokeredMessageBodyConverter, JsonUnicodeBodyConverter>();
            chatterBuilder.Services.AddSingleton(options);

            return chatterBuilder;
        }
    }
}
