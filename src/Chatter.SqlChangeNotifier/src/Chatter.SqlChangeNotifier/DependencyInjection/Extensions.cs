using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.SqlChangeNotifier;
using Chatter.SqlChangeNotifier.Configuration;
using Microsoft.Extensions.Logging;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        public static SqlServiceBrokerOptionsBuilder AddSqlServiceBrokerOptions(this IServiceCollection services)
            => new SqlServiceBrokerOptionsBuilder(services);

        public static IChatterBuilder AddSqlChangeNotifier<TNotificationData>(this IChatterBuilder chatterBuilder, Action<SqlServiceBrokerOptionsBuilder> optionsBuilder)
            where TNotificationData : class, IEvent
        {
            var builder = chatterBuilder.Services.AddSqlServiceBrokerOptions();
            optionsBuilder?.Invoke(builder);
            SqlServiceBrokerOptions options = builder.Build();

            chatterBuilder.Services.AddScoped(sp =>
            {
                var md = sp.GetRequiredService<IMessageDispatcher>();
                var logger = sp.GetRequiredService<ILogger<SqlServiceBrokerReceiver<TNotificationData>>>();
                return new SqlServiceBrokerReceiver<TNotificationData>(options, md, logger);
            });

            chatterBuilder.Services.AddHostedService<TableWatcher<TNotificationData>>();

            return chatterBuilder;
        }
    }
}
