using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.SqlTableWatcher.Configuration;
using Chatter.SqlTableWatcher.Scripts;
using Chatter.TableWatcher;
using Microsoft.AspNetCore.Builder;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        internal static SqlTableWatcherOptionsBuilder AddSqlTableWatcherOptionsBuilder(this IServiceCollection services)
            => new SqlTableWatcherOptionsBuilder(services);

        public static IChatterBuilder AddSqlTableWatcher<TRowChangedData>(this IChatterBuilder builder,
                                                                          Action<SqlTableWatcherOptionsBuilder> optionsBuilder,
                                                                          string tableWatcherQueueName = "",
                                                                          string errorQueueName = null,
                                                                          TransactionMode transactionMode = TransactionMode.ReceiveOnly)
            where TRowChangedData : class, IMessage, new()
        {
            var tableWatcherOptions = builder.Services.AddSqlTableWatcherOptionsBuilder();
            optionsBuilder?.Invoke(tableWatcherOptions);
            SqlTableWatcherOptions options = tableWatcherOptions.Build();

            builder.Services.AddScoped<ISqlDependencyManager>(sp =>
            {
                return new SqlDependencyManager(options);
            });

            builder.AddSqlServiceBroker(ssbBuilder =>
            {
                var receiver = string.IsNullOrWhiteSpace(tableWatcherQueueName) ? $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{typeof(TRowChangedData).Name}" : tableWatcherQueueName;
                ssbBuilder.AddSqlServiceBrokerOptions(options.ConnectionString,
                                                      options.ServiceBrokerOptions.MessageBodyType,
                                                      options.ServiceBrokerOptions.ReceiverTimeoutInMilliseconds,
                                                      options.ServiceBrokerOptions.ConversationLifetimeInSeconds,
                                                      options.ServiceBrokerOptions.ConversationEncryption,
                                                      options.ServiceBrokerOptions.CleanupOnEndConversation)
                          .AddQueueReceiver<ProcessTableChangesCommand<TRowChangedData>>(receiver, errorQueuePath: errorQueueName, transactionMode: transactionMode);
            });

            if (options.ProcessTableChangesViaChatter)
            {
                builder.Services.Replace<IBrokeredMessageReceiver<ProcessTableChangesCommand<TRowChangedData>>, TableChangeReceiver<ProcessTableChangesCommand<TRowChangedData>, TRowChangedData>>(ServiceLifetime.Scoped);
            }

            return builder;
        }

        public static IApplicationBuilder UseTableWatcherSqlMigrations<TRowChangedData>(this IApplicationBuilder applicationBuilder)
        {
            using var scope = applicationBuilder.ApplicationServices.CreateScope();
            var sdm = scope.ServiceProvider.GetRequiredService<ISqlDependencyManager>();

            var receiverName = typeof(TRowChangedData).Name;
            var conversationQueueName = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{receiverName}";
            var conversationServiceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{receiverName}";
            var conversationTriggerName = $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{receiverName}";
            var installNotificationsStoredProcName = $"{ChatterServiceBrokerConstants.ChatterInstallNotificationsPrefix}{receiverName}";
            var uninstallNotificationsStoredProcName = $"{ChatterServiceBrokerConstants.ChatterUninstallNotificationsPrefix}{receiverName}";

            sdm.UninstallSqlDependencies(uninstallNotificationsStoredProcName);
            sdm.InstallSqlDependencies(installNotificationsStoredProcName, uninstallNotificationsStoredProcName, conversationQueueName, conversationServiceName, conversationTriggerName);

            return applicationBuilder;
        }
    }
}
