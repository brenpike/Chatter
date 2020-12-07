using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;
using Microsoft.Extensions.Logging;
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

            chatterBuilder.Services.Replace<IMessagingInfrastructureDispatcher, SqlServiceBrokerSender>(ServiceLifetime.Scoped);
            chatterBuilder.Services.Replace<IBrokeredMessageBodyConverter, JsonUnicodeBodyConverter>(ServiceLifetime.Scoped);
            chatterBuilder.Services.AddSingleton(options);
            chatterBuilder.Services.Replace<IMessagingInfrastructureReceiver, SqlServiceBrokerReceiver>(ServiceLifetime.Scoped);

            //chatterBuilder.Services.Replace<IBrokeredMessagePathBuilder, AzureServiceBusEntityPathBuilder>(ServiceLifetime.Scoped);

            return chatterBuilder;
        }

        public static IChatterBuilder AddSqlTableNotification<TNotificationData>(this IChatterBuilder chatterBuilder, Action<SqlServiceBrokerOptionsBuilder> optionsBuilder)
            where TNotificationData : class, IEvent
        {
            var builder = chatterBuilder.Services.AddSqlServiceBrokerOptions();
            optionsBuilder?.Invoke(builder);
            SqlServiceBrokerOptions options = builder.Build();

            chatterBuilder.Services.AddScoped(sp =>
            {
                var md = sp.GetRequiredService<IMessageDispatcher>();
                var logger = sp.GetRequiredService<ILogger<SqlTableWatcherReceiver<TNotificationData>>>();
                return new SqlTableWatcherReceiver<TNotificationData>(options, md, logger);
            });

            chatterBuilder.Services.AddHostedService<TableWatcher<TNotificationData>>();

            return chatterBuilder;
        }
    }
}
