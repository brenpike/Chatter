using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.SqlChangeFeed.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlChangeFeed.DependencyInjection
{
    public static class SqlChangeFeedExtensions

    {
        private sealed class RowChangedDataPlaceholder : IMessage
        {
        }

        // INVARIANT: the row-type overload dispatches to the generic AddSqlChangeFeed<TRowChangedData> and raises
        // every refusal as itself, not wrapped in a TargetInvocationException. The generic definition is bound by
        // the compiler through a method group over a placeholder row type, never looked up by name, and the call
        // is made with BindingFlags.DoNotWrapExceptions. Oracles, in WhenAddingSqlChangeFeed: the "row type
        // overload" rows of MustLeaveTheServiceCollectionAndDiscoveredReceiversExactlyAsTheyWereWhenAFeedIsRefused,
        // MustRefuseANullRowTypeNamingTheRowTypeParameter and MustRefuseARowTypeThatIsNotAMessageAsAnInvalidArgument.
        // Mutations, measured: invoking with BindingFlags.Default reddens exactly the seven "row type overload"
        // rows; deleting the null guard reddens only the null-row-type fact. The not-a-message fact was already green
        // before the definition was compiler-bound, because MakeGenericMethod raises that ArgumentException itself.
        // NOT covered, and no test pins it: reverting to a name-based lookup of the first generic AddSqlChangeFeed
        // keeps every test green (measured), so dispatch to a different generic overload added later goes unnoticed
        // by the tests; only the compiler binding below prevents it.
        private static readonly MethodInfo _addSqlChangeFeedDefinition =
            new Func<IChatterBuilder, string, string, string, Action<SqlChangeFeedOptionsBuilder>, IChatterBuilder>(AddSqlChangeFeed<RowChangedDataPlaceholder>)
                .Method
                .GetGenericMethodDefinition();

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
            if (rowChangedDataType is null)
            {
                throw new ArgumentNullException(nameof(rowChangedDataType));
            }

            _addSqlChangeFeedDefinition.MakeGenericMethod(rowChangedDataType)
                                       .Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null,
                                               new object[] { builder, connectionString, databaseName, tableName, optionsBuilder },
                                               culture: null);

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
            // INVARIANT: this method makes no write of its own before its last refusal. The options are built and the
            // object names derived first, then the one AddSqlServiceBroker call defers the queue receiver, the
            // ISqlDependencyManager registration and the ChangeFeedReceiver Replace, in that order, so a refused
            // AddSqlServiceBroker runs none of them and the Replace (RemoveAll then Add) runs after the receiver it
            // replaces. Oracles, in WhenAddingSqlChangeFeed:
            // MustLeaveTheServiceCollectionAndDiscoveredReceiversExactlyAsTheyWereWhenAFeedIsRefused and
            // MustRegisterTheChangeFeedReceiverAsTheFeedsOnlyReceiverWhenRowChangeEventsAreEmitted. Mutations,
            // measured: writing the ISqlDependencyManager registration between Build() and DeriveFrom reddens the
            // colliding object names, blank message body type and divergent transport rows; writing the Replace
            // directly before AddSqlServiceBroker reddens the blank message body type and divergent transport rows and
            // the only-receiver fact; deferring the Replace before the queue receiver reddens only the only-receiver
            // fact. The blank connection string, blank table name, malformed connection string and no database rows
            // stay green under every one of these mutations: their refusal is raised by the options builder's
            // constructor or Build(), before any write. The discovered-receiver assertion reddened under none of them,
            // and no mutation reddens any other unit test in this test project.
            // NOT covered, and no test pins it: a write made through the public SqlChangeFeedOptionsBuilder.Services
            // property from inside the options delegate lands immediately, before any refusal.
            var changeFeedOptions = builder.Services.AddSqlChangeFeedOptionsBuilder(connectionString, tableName, databaseName);
            optionsBuilder?.Invoke(changeFeedOptions);
            var (options, objectNames) = BuildChangeFeed(typeof(TRowChangedData), changeFeedOptions.Build);

            builder.AddSqlServiceBroker(ssbBuilder => ConfigureChangeFeedTransport<TRowChangedData>(ssbBuilder, options, objectNames));

            return builder;
        }

        private static (SqlChangeFeedOptions Options, ChangeFeedObjectNames ObjectNames) BuildChangeFeed(Type rowChangedDataType, Func<SqlChangeFeedOptions> buildOptions)
        {
            var options = buildOptions();
            return (options, ChangeFeedObjectNames.DeriveFrom(rowChangedDataType, options));
        }

        private static void ConfigureChangeFeedTransport<TRowChangedData>(SqlServiceBrokerOptionsBuilder ssbBuilder, SqlChangeFeedOptions options, ChangeFeedObjectNames objectNames)
            where TRowChangedData : class, IMessage, new()
        {
            void RegisterSqlDependencyManager(IServiceCollection services)
                => services.AddIfNotRegistered<ISqlDependencyManager<TRowChangedData>>(ServiceLifetime.Scoped, sp => new SqlDependencyManager<TRowChangedData>(options));

            void RegisterChangeFeedReceiver(IServiceCollection services)
                => services.Replace<IBrokeredMessageReceiver<ProcessChangeFeedCommand<TRowChangedData>>, ChangeFeedReceiver<TRowChangedData>>(ServiceLifetime.Scoped);

            // INVARIANT: the receive-attempt limit configured through WithMaxReceiveAttempts reaches the ReceiverOptions
            // of the feed's queue receiver, for both AddSqlChangeFeed overloads. Pinned by the two configured-value facts
            // in WhenAddingSqlChangeFeed; dropping the maxReceiveAttempts argument below reddens both (measured).
            ssbBuilder.AddSqlServiceBrokerOptions(options.ServiceBrokerOptions)
                      .AddQueueReceiver<ProcessChangeFeedCommand<TRowChangedData>>(objectNames.ConversationQueueName,
                                                                                     errorQueuePath: options.ReceiverOptions.ErrorQueuePath,
                                                                                     transactionMode: options.ReceiverOptions.TransactionMode,
                                                                                     deadLetterServicePath: objectNames.ConversationDeadLetterServiceName,
                                                                                     maxReceiveAttempts: options.ReceiverOptions.MaxReceiveAttempts)
                      .DeferRegistration(RegisterSqlDependencyManager);

            if (options.ProcessChangeFeedCommandViaChatter)
            {
                ssbBuilder.DeferRegistration(RegisterChangeFeedReceiver);
            }
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
