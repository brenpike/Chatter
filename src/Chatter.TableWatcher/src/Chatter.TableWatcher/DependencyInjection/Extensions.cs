using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.SqlTableWatcher.Configuration;
using Chatter.SqlTableWatcher.Scripts;
using Chatter.TableWatcher;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        internal static SqlTableWatcherOptionsBuilder AddSqlTableWatcherOptionsBuilder(this IServiceCollection services)
            => new SqlTableWatcherOptionsBuilder(services);

        public static IChatterBuilder AddSqlTableWatcher<TRowChangedData>(this IChatterBuilder builder, Action<SqlTableWatcherOptionsBuilder> optionsBuilder)
            where TRowChangedData : class, IMessage, new()
        {
            var tableWatcherOptions = builder.Services.AddSqlTableWatcherOptionsBuilder();
            optionsBuilder?.Invoke(tableWatcherOptions);
            SqlTableWatcherOptions options = tableWatcherOptions.Build();

            //TODO: when to install/uninstall using ISqlDependencyManager? does it have to be done before sqlservicebrokerreceiver starts? IApplicationBuilder?
            builder.Services.AddIfNotRegistered<ISqlDependencyManager, SqlDependencyManager>(ServiceLifetime.Scoped);

            builder.AddSqlServiceBroker(ssbBuilder =>
            {
                //TODO: where to keep constants for sql dependencies (i.e., names of queues, services, triggers, install/uninstall procs)? Add to options with defaults?
                var receiver = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{typeof(TRowChangedData).Name}";
                //TODO: add params needed for sqlservicebrokeroptions (messageBodyType, conversationLifetimeInSeconds, etc) and AddQueueReceiver (errorQueue, description, etc)
                ssbBuilder.AddSqlServiceBrokerOptions(options.ConnectionString)
                          .AddQueueReceiver<ProcessRowChangeCommand<TRowChangedData>>(receiver);
            });

            //TODO: add option to allow ProcessRowChangeCommand<TRowChangedData> to be processed by chatter user (i.e., don't do this registration)
            builder.Services.Replace<IBrokeredMessageReceiver<ProcessRowChangeCommand<TRowChangedData>>, TableChangeReceiver<ProcessRowChangeCommand<TRowChangedData>, TRowChangedData>>(ServiceLifetime.Scoped);

            return builder;
        }
    }
}
