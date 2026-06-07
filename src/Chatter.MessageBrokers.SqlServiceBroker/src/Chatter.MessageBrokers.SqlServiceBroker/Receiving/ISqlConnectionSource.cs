using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    /// <summary>
    /// Internal port over the single operation the <see cref="SqlServiceBrokerReceiver"/> and
    /// <see cref="Chatter.MessageBrokers.SqlServiceBroker.Sending.SqlServiceBrokerSender"/> need to
    /// originate a database connection: materialising the configured connection string into an open
    /// <see cref="SqlConnection"/>. The production adapter
    /// (<see cref="SqlClientConnectionSource"/>) is the sole place the connection string is turned
    /// into a connection; an in-memory adapter is used to pin receive/send behavior in tests without
    /// a live Service Broker.
    /// </summary>
    /// <remarks>
    /// The port owns connection CREATION ONLY. Transaction lifecycle (begin/commit/rollback) and the
    /// sender's context-transaction reuse path (which bypasses the source entirely) remain in the
    /// receiver/sender adapters, so the source must not assume it is the sole connection origin.
    /// </remarks>
    internal interface ISqlConnectionSource
    {
        /// <summary>Creates a new <see cref="SqlConnection"/> from the configured connection string and opens it.</summary>
        Task<SqlConnection> OpenAsync(CancellationToken cancellationToken);
    }
}
