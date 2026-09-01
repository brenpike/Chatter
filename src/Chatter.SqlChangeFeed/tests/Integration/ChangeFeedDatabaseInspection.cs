using System;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // ADO.NET helper for the DATABASE-level facts the privilege-posture proofs assert about, which the per-table
    // ChangeFeedTableProvisioning helper does not cover: who owns a database, how to give one a deliberately
    // non-'sa' owner so an "owner unchanged" assertion cannot pass vacuously, and how to hold a genuinely competing
    // open transaction on a database while the production migration runs against it.
    //
    // Mirrors the ChangeFeedTableProvisioning ExecuteNonQueryAsync style. Database, login and table names are test
    // constants, never caller input.
    internal static class ChangeFeedDatabaseInspection
    {
        // Reads the database's owner as a login name (SUSER_SNAME over sys.databases.owner_sid). Returns null when
        // the database does not exist or its owner_sid maps to no login, so a caller comparing a before value to an
        // after value sees the difference rather than an exception.
        public static async Task<string> GetDatabaseOwnerAsync(string masterConnectionString, string databaseName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SUSER_SNAME(owner_sid) FROM sys.databases WHERE name = @name;";
            command.Parameters.Add(new SqlParameter("@name", databaseName));
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is null || result == DBNull.Value ? null : (string)result;
        }

        // Idempotently creates loginName and transfers ownership of databaseName to it. The container's own login
        // ('sa') owns every database it creates, so asserting ownership is unchanged across the migration would pass
        // vacuously against the very value the removed ALTER AUTHORIZATION used to force; starting from a DIFFERENT
        // owner is what makes that assertion mean something.
        public static async Task AssignDatabaseOwnerAsync(string masterConnectionString, string databaseName, string loginName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // The login is never authenticated as, only owned-by; a per-run random password keeps a usable
            // credential out of source. CHECK_POLICY is off so the container's policy cannot reject it.
            var password = "Chatter!" + Guid.NewGuid().ToString("N");

            await ExecuteNonQueryAsync(connection,
                $"IF SUSER_ID('{loginName}') IS NULL CREATE LOGIN [{loginName}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF;",
                cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"ALTER AUTHORIZATION ON DATABASE::[{databaseName}] TO [{loginName}];",
                cancellationToken).ConfigureAwait(false);
        }

        // Opens a SECOND session on the database the migration will run against and leaves an UNCOMMITTED insert
        // open on scratchTableName. Without WITH ROLLBACK IMMEDIATE the migration's ENABLE_BROKER waits on this
        // session indefinitely. The caller owns the returned connection and must dispose it (disposal rolls the
        // transaction back) even when an assertion fails.
        public static async Task<SqlConnection> OpenSessionHoldingAnOpenTransactionAsync(string connectionString, string scratchTableName, CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await ExecuteNonQueryAsync(connection,
                    "BEGIN TRANSACTION; " +
                    $"INSERT INTO dbo.[{scratchTableName}] (Id, Name, Value) VALUES (1, 'competing', 'session');",
                    cancellationToken).ConfigureAwait(false);

                return connection;
            }
            catch (Exception)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
