using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.SqlTableWatcher.Configuration
{
    public class SqlTableWatcherOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private SqlTableWatcherOptions _sqlServiceBrokerOptions;

        internal SqlTableWatcherOptionsBuilder(IServiceCollection services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public SqlTableWatcherOptionsBuilder AddOptions(SqlTableWatcherOptions options)
        {
            _sqlServiceBrokerOptions = options;
            return this;
        }

        public SqlTableWatcherOptionsBuilder AddOptions(Func<SqlTableWatcherOptions> optionsBuidler)
        {
            _sqlServiceBrokerOptions = optionsBuidler();
            return this;
        }

        public SqlTableWatcherOptionsBuilder AddOptions(string connectionString,
                                                         string databaseName,
                                                         string tableName,
                                                         string schemaName = "dbo",
                                                         NotificationTypes listenerType =
                                                                    NotificationTypes.Insert | NotificationTypes.Update | NotificationTypes.Delete)
        {
            _sqlServiceBrokerOptions = new SqlTableWatcherOptions(connectionString, databaseName, tableName, schemaName, listenerType);
            return this;
        }

        internal SqlTableWatcherOptions Build()
        {
            if (_sqlServiceBrokerOptions is null)
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions),
                    $"Use an overload of {nameof(AddOptions)} to configure {typeof(SqlTableWatcherOptions).Name}");
            }

            if (string.IsNullOrWhiteSpace(_sqlServiceBrokerOptions.ConnectionString))
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions.ConnectionString), "A connection string is required.");
            }

            if (string.IsNullOrWhiteSpace(_sqlServiceBrokerOptions.DatabaseName))
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions.DatabaseName), "A database is required.");
            }

            if (string.IsNullOrWhiteSpace(_sqlServiceBrokerOptions.TableName))
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions.TableName), "The name of a table is required.");
            }

            if (string.IsNullOrWhiteSpace(_sqlServiceBrokerOptions.SchemaName))
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions.SchemaName), "A schema is required for database objects.");
            }

            return _sqlServiceBrokerOptions;
        }
    }
}
