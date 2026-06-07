using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving
{
    // In-memory ISqlConnectionSource double used to drive SqlServiceBrokerReceiver's/SqlServiceBrokerSender's
    // connection-origination path without a live Service Broker. Hands back a caller-supplied SqlConnection
    // (or an unopened new SqlConnection(), whose CreateCommand() works without a server); records OpenAsync
    // call count and the last cancellation token so tests can assert the port was used.
    internal class InMemorySqlConnectionSource : ISqlConnectionSource
    {
        private readonly SqlConnection _connection;

        public InMemorySqlConnectionSource(SqlConnection connection = null)
        {
            _connection = connection ?? new SqlConnection();
        }

        public int OpenCount { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(_connection);
        }
    }
}
