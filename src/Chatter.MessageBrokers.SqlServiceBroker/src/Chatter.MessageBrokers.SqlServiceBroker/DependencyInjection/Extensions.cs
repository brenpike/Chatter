using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
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

        public static IChatterBuilder AddSqlServiceBroker(this IChatterBuilder builder, Action<SqlServiceBrokerOptionsBuilder> optionsBuilder = null)
        {
            var optBuilder = builder.Services.AddSqlServiceBrokerOptions();
            optionsBuilder?.Invoke(optBuilder);
            var options = optBuilder.Build();

            builder.Services.AddScoped<SqlServiceBrokerReceiver>();
            builder.Services.AddSingleton<SqlServiceBrokerSenderFactory>();

            builder.Services.AddScoped<SqlServiceBrokerSender>();
            builder.Services.AddSingleton<SqlServiceBrokerReceiverFactory>();

            builder.Services.AddSingleton<IMessagingInfrastructure>(sp =>
            {
                var sender = sp.GetRequiredService<SqlServiceBrokerSenderFactory>();
                var receiver = sp.GetRequiredService<SqlServiceBrokerReceiverFactory>();
                return new MessagingInfrastructure(SSBMessageContext.InfrastructureType, receiver, sender);
            });

            builder.Services.AddScoped<IBrokeredMessageBodyConverter, JsonUnicodeBodyConverter>();
            builder.Services.AddSingleton(options);

            return builder;
        }
    }
}
