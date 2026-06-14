using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using System;
using Microsoft.Data.SqlClient;
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
            // INVARIANT: SSB's RECEIVE-then-settle dialog holds a WAITFOR (RECEIVE ...) reader open on the
            // connection while issuing settle/deadletter/forward commands on the SAME connection, which requires
            // MARS. Microsoft.Data.SqlClient enforces this (System.Data.SqlClient tolerated it), so force MARS on
            // by construction regardless of the consumer-supplied connection string — SSB cannot function without it.
            var builder = new SqlConnectionStringBuilder(_ssbOptions.ConnectionString)
            {
                MultipleActiveResultSets = true,
            };
            var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }
}
