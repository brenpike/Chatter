using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.SqlChangeFeed.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

            var objectNames = ChangeFeedObjectNames.DeriveFrom(typeof(TRowChangedData), options);

            builder.AddSqlServiceBroker(ssbBuilder =>
            {
                ssbBuilder.AddSqlServiceBrokerOptions(options.ServiceBrokerOptions)
                          .AddQueueReceiver<ProcessChangeFeedCommand<TRowChangedData>>(objectNames.ConversationQueueName,
                                                                                         errorQueuePath: options.ReceiverOptions.ErrorQueuePath,
                                                                                         transactionMode: options.ReceiverOptions.TransactionMode,
                                                                                         deadLetterServicePath: objectNames.ConversationDeadLetterServiceName);
            });

            if (options.ProcessChangeFeedCommandViaChatter)
            {
                builder.Services.Replace<IBrokeredMessageReceiver<ProcessChangeFeedCommand<TRowChangedData>>, ChangeFeedReceiver<TRowChangedData>>(ServiceLifetime.Scoped);
            }

            return builder;
        }

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <typeparam name="TRowChangedData">The row type to use Sql migrations for</typeparam>
        /// <param name="provider">The service provider</param>
        /// <param name="token">A token to observe while waiting for the migration to complete</param>
        /// <returns></returns>
        [Obsolete("Blocking on the asynchronous installation risks deadlocking a caller with a single-threaded SynchronizationContext. Use UseChangeFeedSqlMigrationsAsync instead.")]
        public static IServiceProvider UseChangeFeedSqlMigrations<TRowChangedData>(this IServiceProvider provider, CancellationToken token = default)
            => provider.UseChangeFeedSqlMigrations(typeof(TRowChangedData), token);

        /// <summary>
        /// Deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <param name="provider">The service provider</param>
        /// <param name="rowChangedDataType">The row type to use Sql migrations for</param>
        /// <param name="token">A token to observe while waiting for the migration to complete</param>
        [Obsolete("Blocking on the asynchronous installation risks deadlocking a caller with a single-threaded SynchronizationContext. Use UseChangeFeedSqlMigrationsAsync instead.")]
        public static IServiceProvider UseChangeFeedSqlMigrations(this IServiceProvider provider, Type rowChangedDataType, CancellationToken token = default)
        {
            using var scope = provider.CreateScope();
            var sdm = (ISqlDependencyManager)scope.ServiceProvider.GetRequiredService(typeof(ISqlDependencyManager<>).MakeGenericType(rowChangedDataType));

            var objectNames = ChangeFeedObjectNames.DeriveFrom(rowChangedDataType, sdm.Options);

            // INVARIANT: the installation is started on the thread pool so its awaits capture no ambient
            // SynchronizationContext. Blocking on it here therefore cannot deadlock a caller running under a
            // single-threaded context whose only pump thread is this one. The token stays an argument to
            // InstallSqlDependencies and is deliberately NOT passed to Task.Run, which would replace the
            // installation's own cancellation behaviour with a pre-execution scheduler cancellation.
            Task.Run(() => sdm.InstallSqlDependencies(objectNames.InstallChangeFeedStoredProcName,
                                                      objectNames.UninstallChangeFeedStoredProcName,
                                                      objectNames.ConversationQueueName,
                                                      objectNames.ConversationServiceName,
                                                      objectNames.ConversationTriggerName,
                                                      objectNames.ConversationDeadLetterQueueName,
                                                      objectNames.ConversationDeadLetterServiceName,
                                                      token)).GetAwaiter().GetResult();

            return provider;
        }

        /// <summary>
        /// Asynchronously deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <typeparam name="TRowChangedData">The row type to use Sql migrations for</typeparam>
        /// <param name="provider">The service provider</param>
        /// <param name="token">A token to observe while waiting for the migration to complete</param>
        /// <returns>A task that completes when the migration has finished</returns>
        public static Task UseChangeFeedSqlMigrationsAsync<TRowChangedData>(this IServiceProvider provider, CancellationToken token = default)
            => provider.UseChangeFeedSqlMigrationsAsync(typeof(TRowChangedData), token);

        /// <summary>
        /// Asynchronously deploys the SQL and SQL Service Broker dependencies required for table changes to be emitted
        /// </summary>
        /// <param name="provider">The service provider</param>
        /// <param name="rowChangedDataType">The row type to use Sql migrations for</param>
        /// <param name="token">A token to observe while waiting for the migration to complete</param>
        /// <returns>A task that completes when the migration has finished</returns>
        public static async Task UseChangeFeedSqlMigrationsAsync(this IServiceProvider provider, Type rowChangedDataType, CancellationToken token = default)
        {
            using var scope = provider.CreateScope();
            var sdm = (ISqlDependencyManager)scope.ServiceProvider.GetRequiredService(typeof(ISqlDependencyManager<>).MakeGenericType(rowChangedDataType));

            var objectNames = ChangeFeedObjectNames.DeriveFrom(rowChangedDataType, sdm.Options);

            await sdm.InstallSqlDependencies(objectNames.InstallChangeFeedStoredProcName,
                                             objectNames.UninstallChangeFeedStoredProcName,
                                             objectNames.ConversationQueueName,
                                             objectNames.ConversationServiceName,
                                             objectNames.ConversationTriggerName,
                                             objectNames.ConversationDeadLetterQueueName,
                                             objectNames.ConversationDeadLetterServiceName,
                                             token).ConfigureAwait(false);
        }
    }
}
