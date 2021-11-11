﻿using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.SqlTableWatcher;
using Chatter.SqlTableWatcher.Configuration;
using Chatter.SqlTableWatcher.Scripts;
using Microsoft.AspNetCore.Builder;
using System;
using System.Linq;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class SqlTableWatcherExtensions

    {
        internal static SqlTableWatcherOptionsBuilder AddSqlTableWatcherOptionsBuilder(this IServiceCollection services, string connectionString, string tableName, string databaseName = null)
            => new SqlTableWatcherOptionsBuilder(services, connectionString, databaseName, tableName);

        /// <summary>
        /// Configures a watcher to monitor a sql table for changes
        /// </summary>
        /// <param name="rowChangedDataType">A type implementing <see cref="IMessage"/> that maps to a row that changed in the target database</param>
        /// <param name="connectionString">The connection string of the database containing the table to watch for changes</param>
        /// <param name="databaseName">The database containing the table to watch</param>
        /// <param name="tableName">The name of the table to watch</param>
        /// <param name="optionsBuilder">An optional builder allowing more complex table watcher configuration</param>
        public static IChatterBuilder AddSqlTableWatcher(this IChatterBuilder builder,
                                                         Type rowChangedDataType,
                                                         string connectionString,
                                                         string databaseName,
                                                         string tableName,
                                                         Action<SqlTableWatcherOptionsBuilder> optionsBuilder = null)
        {
            typeof(SqlTableWatcherExtensions).GetMethods()
                             .Where(m => m.IsGenericMethod
                                         && m.Name == nameof(AddSqlTableWatcher))
                             .FirstOrDefault()
                             .MakeGenericMethod(rowChangedDataType)
                             .Invoke(null, new object[] { builder, connectionString, databaseName, tableName, optionsBuilder });

            return builder;
        }

        /// <summary>
        /// Configures a watcher to monitor a sql table for changes
        /// </summary>
        /// <typeparam name="TRowChangedData">The <see cref="IMessage"/> representing the state of a changed row in the table being watched</typeparam>
        /// <param name="connectionString">The connection string of the database containing the table to watch for changes</param>
        /// <param name="databaseName">The database containing the table to watch</param>
        /// <param name="tableName">The name of the table to watch</param>
        /// <param name="optionsBuilder">An optional builder allowing more complex table watcher configuration</param>
        /// <returns><see cref="IChatterBuilder"/></returns>
        public static IChatterBuilder AddSqlTableWatcher<TRowChangedData>(this IChatterBuilder builder,
                                                                          string connectionString,
                                                                          string databaseName,
                                                                          string tableName,
                                                                          Action<SqlTableWatcherOptionsBuilder> optionsBuilder = null)
            where TRowChangedData : class, IMessage, new()
        {
            var tableWatcherOptions = builder.Services.AddSqlTableWatcherOptionsBuilder(connectionString, tableName, databaseName);
            optionsBuilder?.Invoke(tableWatcherOptions);
            SqlTableWatcherOptions options = tableWatcherOptions.Build();

            builder.Services.AddIfNotRegistered<ISqlDependencyManager<TRowChangedData>>(ServiceLifetime.Scoped, sp =>
            {
                return new SqlDependencyManager<TRowChangedData>(options);
            });

            builder.AddSqlServiceBroker(ssbBuilder =>
            {
                var receiver = string.IsNullOrWhiteSpace(options.TableWatcherQueueName) ? $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{typeof(TRowChangedData).Name}" : options.TableWatcherQueueName;
                ssbBuilder.AddSqlServiceBrokerOptions(options.ConnectionString,
                                                      options.ServiceBrokerOptions.MessageBodyType,
                                                      options.ServiceBrokerOptions.ReceiverTimeoutInMilliseconds,
                                                      options.ServiceBrokerOptions.ConversationLifetimeInSeconds,
                                                      options.ServiceBrokerOptions.ConversationEncryption,
                                                      options.ServiceBrokerOptions.CleanupOnEndConversation)
                          .AddQueueReceiver<ProcessTableChangesCommand<TRowChangedData>>(receiver, errorQueuePath: options.ReceiverOptions.ErrorQueuePath, transactionMode: options.ReceiverOptions.TransactionMode);
            });

            if (options.ProcessTableChangesViaChatter)
            {
                builder.Services.Replace<IBrokeredMessageReceiver<ProcessTableChangesCommand<TRowChangedData>>, TableChangeReceiver<ProcessTableChangesCommand<TRowChangedData>, TRowChangedData>>(ServiceLifetime.Scoped);
            }

            return builder;
        }

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <typeparam name="TRowChangedData">The row type to use Sql migrations for</typeparam>
        /// <param name="applicationBuilder">The application builder</param>
        /// <returns></returns>
        public static IApplicationBuilder UseTableWatcherSqlMigrations<TRowChangedData>(this IApplicationBuilder applicationBuilder)
            => applicationBuilder.UseTableWatcherSqlMigrations(typeof(TRowChangedData));

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <param name="applicationBuilder">The application builder</param>
        /// <param name="rowChangedDataType">The row type to use Sql migrations for</param>
        /// <returns></returns>
        public static IApplicationBuilder UseTableWatcherSqlMigrations(this IApplicationBuilder applicationBuilder, Type rowChangedDataType)
        {
            using var scope = applicationBuilder.ApplicationServices.CreateScope();
            var sdm = (ISqlDependencyManager)scope.ServiceProvider.GetRequiredService(typeof(ISqlDependencyManager<>).MakeGenericType(rowChangedDataType));

            var receiverName = rowChangedDataType.Name;
            var conversationQueueName = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{receiverName}";
            var conversationServiceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{receiverName}";
            var conversationTriggerName = $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{receiverName}";
            var installNotificationsStoredProcName = $"{ChatterServiceBrokerConstants.ChatterInstallNotificationsPrefix}{receiverName}";
            var uninstallNotificationsStoredProcName = $"{ChatterServiceBrokerConstants.ChatterUninstallNotificationsPrefix}{receiverName}";

            sdm.InstallSqlDependencies(installNotificationsStoredProcName, uninstallNotificationsStoredProcName, conversationQueueName, conversationServiceName, conversationTriggerName);

            return applicationBuilder;
        }
    }
}