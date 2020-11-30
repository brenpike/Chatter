using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.SqlServiceBroker.Configuration
{
    public class SqlServiceBrokerOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private SqlServiceBrokerOptions _sqlServiceBrokerOptions;

        internal SqlServiceBrokerOptionsBuilder(IServiceCollection services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public SqlServiceBrokerOptionsBuilder AddOptions(SqlServiceBrokerOptions options)
        {
            _sqlServiceBrokerOptions = options;
            return this;
        }

        public SqlServiceBrokerOptionsBuilder AddOptions(Func<SqlServiceBrokerOptions> optionsBuidler)
        {
            _sqlServiceBrokerOptions = optionsBuidler();
            return this;
        }

        public SqlServiceBrokerOptionsBuilder AddOptions(string connectionString,
                                                         string databaseName,
                                                         string tableName,
                                                         string schemaName = "dbo",
                                                         NotificationTypes listenerType =
                                                                    NotificationTypes.Insert | NotificationTypes.Update | NotificationTypes.Delete,
                                                         bool receiveDetails = true)
        {
            _sqlServiceBrokerOptions = new SqlServiceBrokerOptions(connectionString, databaseName, tableName, schemaName, listenerType);
            return this;
        }

        internal SqlServiceBrokerOptions Build()
        {
            if (_sqlServiceBrokerOptions is null)
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions),
                    $"Use an overload of {nameof(AddOptions)} to configure {typeof(SqlServiceBrokerOptions).Name}");
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
