using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.TableWatcher;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.SqlTableWatcher.Configuration
{
    public class SqlTableWatcherOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private SqlTableWatcherOptions _sqlTableWatcherOptions;
        private SqlServiceBrokerOptions _sqlServiceBrokerOptions;
        private const string _defaultMessageBodyType = "application/json; charset=utf-16";

        internal SqlTableWatcherOptionsBuilder(IServiceCollection services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public SqlTableWatcherOptionsBuilder AddOptions(SqlTableWatcherOptions options)
        {
            _sqlTableWatcherOptions = options;
            return this;
        }

        public SqlTableWatcherOptionsBuilder AddOptions(Func<SqlTableWatcherOptions> optionsBuidler)
        {
            _sqlTableWatcherOptions = optionsBuidler();
            return this;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="connectionString">The connection string of the table to be watched.</param>
        /// <param name="databaseName">The name of the database containing the table to be watched.</param>
        /// <param name="tableName">The table to watch.</param>
        /// <param name="schemaName">The schema of the table to watch. The default is "dbo".</param>
        /// <param name="listenerType">The types of table changes to watch for. By default it listens to inserts, updates and deletes.</param>
        /// <param name="processTableChangesViaChatter">
        /// When true, inserts, updates and deletes made to <see cref="TableName"/> will be processed by Chatter. The consumer will be required to handle
        /// <see cref="RowInsertedEvent{TRowChangeData}"/>, <see cref="RowUpdatedEvent{TRowChangeData}"/>, and <see cref="RowDeletedEvent{TRowChangeData}"/> to receive table changes.
        /// Otherwise, the consumer must handle <see cref="ProcessTableChangesCommand{TRowChangeData}"/> and process the table changes manually.
        /// Defaulted to true.
        /// </param>
        public SqlTableWatcherOptionsBuilder AddOptions(string connectionString,
                                                        string databaseName,
                                                        string tableName,
                                                        string schemaName = "dbo",
                                                        ChangeTypes listenerType =
                                                                   ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete,
                                                        bool processTableChangesViaChatter = true,
                                                        string messageBodyType = _defaultMessageBodyType,
                                                        int receiverTimeoutInMilliseconds = -1,
                                                        int conversationLifetimeInSeconds = int.MaxValue,
                                                        bool coversationEncryption = false,
                                                        bool compressMessageBody = true,
                                                        bool cleanupOnEndConversation = false)
        {
            _sqlTableWatcherOptions = new SqlTableWatcherOptions(connectionString, databaseName, tableName, schemaName, listenerType, processTableChangesViaChatter)
            {
                ServiceBrokerOptions = new SqlServiceBrokerOptions(connectionString, messageBodyType, receiverTimeoutInMilliseconds, conversationLifetimeInSeconds, coversationEncryption, compressMessageBody, cleanupOnEndConversation)
            };
            return this;
        }

        internal SqlTableWatcherOptions Build()
        {
            if (_sqlTableWatcherOptions is null)
            {
                throw new ArgumentNullException(nameof(_sqlTableWatcherOptions),
                    $"Use an overload of {nameof(AddOptions)} to configure {typeof(SqlTableWatcherOptions).Name}");
            }

            if (string.IsNullOrWhiteSpace(_sqlTableWatcherOptions.ConnectionString))
            {
                throw new ArgumentNullException(nameof(_sqlTableWatcherOptions.ConnectionString), "A connection string is required.");
            }

            if (string.IsNullOrWhiteSpace(_sqlTableWatcherOptions.DatabaseName))
            {
                throw new ArgumentNullException(nameof(_sqlTableWatcherOptions.DatabaseName), "A database is required.");
            }

            if (string.IsNullOrWhiteSpace(_sqlTableWatcherOptions.TableName))
            {
                throw new ArgumentNullException(nameof(_sqlTableWatcherOptions.TableName), "The name of a table is required.");
            }

            if (string.IsNullOrWhiteSpace(_sqlTableWatcherOptions.SchemaName))
            {
                throw new ArgumentNullException(nameof(_sqlTableWatcherOptions.SchemaName), "A schema is required for database objects.");
            }

            return _sqlTableWatcherOptions;
        }
    }
}
