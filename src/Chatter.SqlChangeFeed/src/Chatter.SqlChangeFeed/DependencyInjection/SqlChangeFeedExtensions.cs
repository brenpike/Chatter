using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.Scripts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace Chatter.SqlChangeFeed.DependencyInjection
{
    public static class SqlChangeFeedExtensions

    {
        internal static SqlChangeFeedOptionsBuilder AddSqlChangeFeedOptionsBuilder(this IServiceCollection services, string connectionString, string tableName, string databaseName = null)
            => new SqlChangeFeedOptionsBuilder(services, connectionString, databaseName, tableName);

        /// <summary>
        /// Configures a change feed for specified table
        /// </summary>
        /// <param name="rowChangedDataType">A type implementing <see cref="IMessage"/> that maps to a row that changed in the target database</param>
        /// <param name="connectionString">The connection string for the sql server with the database and table to watch for changes</param>
        /// <param name="databaseName">Optional. The database containing the table to watch. If not specified, Database or InitialCatalog of the connectionString will be used.</param>
        /// <param name="tableName">The name of the table to watch</param>
        /// <param name="optionsBuilder">An optional builder allowing more complex change feed configuration</param>
        public static IChatterBuilder AddSqlChangeFeed(this IChatterBuilder builder,
                                                       Type rowChangedDataType,
                                                       string connectionString,
                                                       string databaseName,
                                                       string tableName,
                                                       Action<SqlChangeFeedOptionsBuilder> optionsBuilder = null)
        {
            typeof(SqlChangeFeedExtensions).GetMethods()
                             .Where(m => m.IsGenericMethod
                                         && m.Name == nameof(AddSqlChangeFeed))
                             .FirstOrDefault()
                             .MakeGenericMethod(rowChangedDataType)
                             .Invoke(null, new object[] { builder, connectionString, databaseName, tableName, optionsBuilder });

            return builder;
        }

        /// <summary>
        /// Configures a change feed for specified table
        /// </summary>
        /// <typeparam name="TRowChangedData">The <see cref="IMessage"/> representing the state of a changed row in the table being watched</typeparam>
        /// <param name="connectionString">The connection string for the sql server with the database and table to watch for changes</param>
        /// <param name="databaseName">Optional. The database containing the table to watch. If not specified, Database or InitialCatalog of the connectionString will be used.</param>
        /// <param name="tableName">The name of the table to watch</param>
        /// <param name="optionsBuilder">An optional builder allowing more complex change feed configuration</param>
        /// <returns><see cref="IChatterBuilder"/></returns>
        public static IChatterBuilder AddSqlChangeFeed<TRowChangedData>(this IChatterBuilder builder,
                                                                          string connectionString,
                                                                          string databaseName,
                                                                          string tableName,
                                                                          Action<SqlChangeFeedOptionsBuilder> optionsBuilder = null)
            where TRowChangedData : class, IMessage, new()
        {
            var changeFeedOptions = builder.Services.AddSqlChangeFeedOptionsBuilder(connectionString, tableName, databaseName);
            optionsBuilder?.Invoke(changeFeedOptions);
            SqlChangeFeedOptions options = changeFeedOptions.Build();

            builder.Services.AddIfNotRegistered<ISqlDependencyManager<TRowChangedData>>(ServiceLifetime.Scoped, sp =>
            {
                return new SqlDependencyManager<TRowChangedData>(options);
            });

            builder.AddSqlServiceBroker(ssbBuilder =>
            {
                var receiver = string.IsNullOrWhiteSpace(options.ChangeFeedQueueName) ? $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{typeof(TRowChangedData).Name}" : options.ChangeFeedQueueName;
                var dlq = string.IsNullOrWhiteSpace(options.ChangeFeedDeadLetterServiceName) ? $"{ChatterServiceBrokerConstants.ChatterDeadLetterServicePrefix}{typeof(TRowChangedData).Name}" : options.ChangeFeedDeadLetterServiceName;
                ssbBuilder.AddSqlServiceBrokerOptions(options.ServiceBrokerOptions)
                          .AddQueueReceiver<ProcessChangeFeedCommand<TRowChangedData>>(receiver,
                                                                                         errorQueuePath: options.ReceiverOptions.ErrorQueuePath,
                                                                                         transactionMode: options.ReceiverOptions.TransactionMode,
                                                                                         deadLetterServicePath: dlq);
            });

            if (options.ProcessChangeFeedCommandViaChatter)
            {
                builder.Services.Replace<IBrokeredMessageReceiver<ProcessChangeFeedCommand<TRowChangedData>>, ChangeFeedReceiver<TRowChangedData>>(ServiceLifetime.Scoped);
            }

            return builder;
        }

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for the sql change feed
        /// </summary>
        /// <typeparam name="TRowChangedData">The row type to use Sql migrations for</typeparam>
        /// <param name="applicationBuilder">The application builder</param>
        /// <returns></returns>
        public static IApplicationBuilder UseChangeFeedSqlMigrations<TRowChangedData>(this IApplicationBuilder applicationBuilder)
            => applicationBuilder.UseChangeFeedSqlMigrations(typeof(TRowChangedData));

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for the sql change feed
        /// </summary>
        /// <param name="applicationBuilder">The application builder</param>
        /// <param name="rowChangedDataType">The row type to use Sql migrations for</param>
        public static IApplicationBuilder UseChangeFeedSqlMigrations(this IApplicationBuilder applicationBuilder, Type rowChangedDataType)
        {
            applicationBuilder.ApplicationServices.UseChangeFeedSqlMigrations(rowChangedDataType);
            return applicationBuilder;
        }

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <typeparam name="TRowChangedData">The row type to use Sql migrations for</typeparam>
        /// <param name="provider">The service provider</param>
        /// <returns></returns>
        public static IServiceProvider UseChangeFeedSqlMigrations<TRowChangedData>(this IServiceProvider provider)
            => provider.UseChangeFeedSqlMigrations(typeof(TRowChangedData));

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <param name="provider">The service provider</param>
        /// <param name="rowChangedDataType">The row type to use Sql migrations for</param>
        public static IServiceProvider UseChangeFeedSqlMigrations(this IServiceProvider provider, Type rowChangedDataType)
        {
            using var scope = provider.CreateScope();
            var sdm = (ISqlDependencyManager)scope.ServiceProvider.GetRequiredService(typeof(ISqlDependencyManager<>).MakeGenericType(rowChangedDataType));


            var receiverName = rowChangedDataType.Name;
            var conversationQueueName = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{receiverName}";
            var conversationServiceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{receiverName}";
            var conversationDeadLetterQueueName = $"{ChatterServiceBrokerConstants.ChatterDeadLetterQueuePrefix}{receiverName}";
            var conversationDeadLetterServiceName = $"{ChatterServiceBrokerConstants.ChatterDeadLetterServicePrefix}{receiverName}";
            var conversationTriggerName = $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{receiverName}";
            var installChangeFeedStoredProcName = $"{ChatterServiceBrokerConstants.ChatterInstallChangeFeedPrefix}{receiverName}";
            var uninstallChangeFeedStoredProcName = $"{ChatterServiceBrokerConstants.ChatterUninstallChangeFeedPrefix}{receiverName}";

            sdm.InstallSqlDependencies(installChangeFeedStoredProcName, uninstallChangeFeedStoredProcName, conversationQueueName, conversationServiceName, conversationTriggerName, conversationDeadLetterQueueName, conversationDeadLetterServiceName);

            return provider;
        }
    }
}
