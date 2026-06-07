using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using System;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    /// <summary>
    /// Production <see cref="ISqlConnectionSource"/> that materialises
    /// <see cref="SqlServiceBrokerOptions.ConnectionString"/> into an open <see cref="SqlConnection"/>.
    /// This is the sole place the connection string becomes a live connection.
    /// </summary>
    internal class SqlClientConnectionSource : ISqlConnectionSource
    {
        private readonly SqlServiceBrokerOptions _ssbOptions;

        public SqlClientConnectionSource(SqlServiceBrokerOptions ssbOptions)
        {
            _ssbOptions = ssbOptions ?? throw new ArgumentNullException(nameof(ssbOptions));
        }

        public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(_ssbOptions.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }
}
