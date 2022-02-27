using Chatter.CQRS;
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
        /// <param name="connectionString">The connection string for the sql server with the database and table to watch for changes</param>
        /// <param name="databaseName">Optional. The database containing the table to watch. If not specified, Database or InitialCatalog of the connectionString will be used.</param>
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
        /// <param name="connectionString">The connection string for the sql server with the database and table to watch for changes</param>
        /// <param name="databaseName">Optional. The database containing the table to watch. If not specified, Database or InitialCatalog of the connectionString will be used.</param>
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
                var dlq = string.IsNullOrWhiteSpace(options.TableWatcherDeadLetterServiceName) ? $"{ChatterServiceBrokerConstants.ChatterDeadLetterServicePrefix}{typeof(TRowChangedData).Name}" : options.TableWatcherDeadLetterServiceName;
                ssbBuilder.AddSqlServiceBrokerOptions(options.ServiceBrokerOptions)
                          .AddQueueReceiver<ProcessTableChangesCommand<TRowChangedData>>(receiver,
                                                                                         errorQueuePath: options.ReceiverOptions.ErrorQueuePath,
                                                                                         transactionMode: options.ReceiverOptions.TransactionMode,
                                                                                         deadLetterServicePath: dlq);
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
        public static IApplicationBuilder UseTableWatcherSqlMigrations(this IApplicationBuilder applicationBuilder, Type rowChangedDataType)
        {
            applicationBuilder.ApplicationServices.UseTableWatcherSqlMigrations(rowChangedDataType);
            return applicationBuilder;
        }

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <typeparam name="TRowChangedData">The row type to use Sql migrations for</typeparam>
        /// <param name="provider">The service provider</param>
        /// <returns></returns>
        public static IServiceProvider UseTableWatcherSqlMigrations<TRowChangedData>(this IServiceProvider provider)
            => provider.UseTableWatcherSqlMigrations(typeof(TRowChangedData));

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <param name="provider">The service provider</param>
        /// <param name="rowChangedDataType">The row type to use Sql migrations for</param>
        public static IServiceProvider UseTableWatcherSqlMigrations(this IServiceProvider provider, Type rowChangedDataType)
        {
            using var scope = provider.CreateScope();
            var sdm = (ISqlDependencyManager)scope.ServiceProvider.GetRequiredService(typeof(ISqlDependencyManager<>).MakeGenericType(rowChangedDataType));


            var receiverName = rowChangedDataType.Name;
            var conversationQueueName = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{receiverName}";
            var conversationServiceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{receiverName}";
            var conversationDeadLetterQueueName = $"{ChatterServiceBrokerConstants.ChatterDeadLetterQueuePrefix}{receiverName}";
            var conversationDeadLetterServiceName = $"{ChatterServiceBrokerConstants.ChatterDeadLetterServicePrefix}{receiverName}";
            var conversationTriggerName = $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{receiverName}";
            var installNotificationsStoredProcName = $"{ChatterServiceBrokerConstants.ChatterInstallNotificationsPrefix}{receiverName}";
            var uninstallNotificationsStoredProcName = $"{ChatterServiceBrokerConstants.ChatterUninstallNotificationsPrefix}{receiverName}";

            sdm.InstallSqlDependencies(installNotificationsStoredProcName, uninstallNotificationsStoredProcName, conversationQueueName, conversationServiceName, conversationTriggerName, conversationDeadLetterQueueName, conversationDeadLetterServiceName);

            return provider;
        }
    }
}
