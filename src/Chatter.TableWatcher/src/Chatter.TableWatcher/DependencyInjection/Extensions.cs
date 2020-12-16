using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.SqlTableWatcher;
using Chatter.SqlTableWatcher.Configuration;
using Microsoft.Extensions.Logging;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        internal static SqlTableWatcherOptionsBuilder AddSqlTableWatcherOptionsBuilder(this IServiceCollection services)
            => new SqlTableWatcherOptionsBuilder(services);

        public static IChatterBuilder AddSqlTableNotification<TNotificationData>(this IChatterBuilder chatterBuilder, Action<SqlTableWatcherOptionsBuilder> optionsBuilder)
            where TNotificationData : class, IEvent
        {
            var builder = chatterBuilder.Services.AddSqlTableWatcherOptionsBuilder();
            optionsBuilder?.Invoke(builder);
            SqlTableWatcherOptions options = builder.Build();

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
