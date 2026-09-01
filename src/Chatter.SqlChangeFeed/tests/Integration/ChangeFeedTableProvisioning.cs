using System;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // ADO.NET helper that creates and mutates the watched table for a SqlChangeFeed integration test class. The
    // production change-feed migration installs the trigger ON this table, so the table must exist (with a PRIMARY
    // KEY) before UseChangeFeedSqlMigrations runs. The PK is REQUIRED: the generated trigger builds its message
    // by a FULL OUTER JOIN of INSERTED to DELETED on the table's primary-key columns, so a table without a PK
    // would produce a malformed join and no usable change message.
    //
    // Each test class owns a DISTINCT table name (object names the migration derives come from typeof(TRow).Name,
    // so a shared table/DTO across classes would collide); this helper takes the table name per call. All
    // CREATE/DROP statements are idempotent (guarded against sys.* / OBJECT_ID) so re-runs never throw. Mirrors
    // the SQL Service Broker integration ServiceBrokerProvisioning ExecuteNonQueryAsync style.
    internal static class ChangeFeedTableProvisioning
    {
        // Idempotently creates dbo.[tableName] with the shape the integration row DTOs map to: an Id INT PRIMARY
        // KEY (required by the trigger's FULL OUTER JOIN) plus Name/Value string columns. Safe to call repeatedly.
        public static async Task CreateTableAsync(string connectionString, string tableName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"IF OBJECT_ID('dbo.[{tableName}]', 'U') IS NULL " +
                $"CREATE TABLE dbo.[{tableName}] (" +
                "Id INT NOT NULL PRIMARY KEY, " +
                "Name NVARCHAR(200) NULL, " +
                "Value NVARCHAR(200) NULL);",
                cancellationToken).ConfigureAwait(false);
        }

        // Idempotently creates dbo.[tableName] with the same columns as CreateTableAsync but NO PRIMARY KEY, so the
        // install procedure's precondition gate can be exercised against a real table the trigger could not build a
        // FULL OUTER JOIN for. Id carries a UNIQUE constraint deliberately: the gate must refuse on the absence of a
        // PRIMARY KEY specifically, not on the absence of any key-shaped constraint.
        public static async Task CreateTableWithoutPrimaryKeyAsync(string connectionString, string tableName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"IF OBJECT_ID('dbo.[{tableName}]', 'U') IS NULL " +
                $"CREATE TABLE dbo.[{tableName}] (" +
                "Id INT NOT NULL UNIQUE, " +
                "Name NVARCHAR(200) NULL, " +
                "Value NVARCHAR(200) NULL);",
                cancellationToken).ConfigureAwait(false);
        }

        public static async Task DropTableAsync(string connectionString, string tableName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"IF OBJECT_ID('dbo.[{tableName}]', 'U') IS NOT NULL DROP TABLE dbo.[{tableName}];",
                cancellationToken).ConfigureAwait(false);
        }

        public static async Task InsertRowAsync(string connectionString, string tableName, int id, string name, string value, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"INSERT INTO dbo.[{tableName}] (Id, Name, Value) VALUES (@id, @name, @value);",
                cancellationToken,
                ("@id", id),
                ("@name", (object)name ?? DBNull.Value),
                ("@value", (object)value ?? DBNull.Value)).ConfigureAwait(false);
        }

        public static async Task UpdateRowAsync(string connectionString, string tableName, int id, string name, string value, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"UPDATE dbo.[{tableName}] SET Name = @name, Value = @value WHERE Id = @id;",
                cancellationToken,
                ("@id", id),
                ("@name", (object)name ?? DBNull.Value),
                ("@value", (object)value ?? DBNull.Value)).ConfigureAwait(false);
        }

        public static async Task DeleteRowAsync(string connectionString, string tableName, int id, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"DELETE FROM dbo.[{tableName}] WHERE Id = @id;",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);
        }

        // True when a trigger with the given name exists (OBJECT_ID ... 'TR'). Used by the uninstall test to
        // assert the production migration created, then removed, the change-feed trigger.
        public static async Task<bool> TriggerExistsAsync(string connectionString, string triggerName, CancellationToken cancellationToken)
            => await ScalarExistsAsync(connectionString,
                "SELECT OBJECT_ID('dbo.[' + @name + ']', 'TR');",
                triggerName, cancellationToken).ConfigureAwait(false);

        // True when a Service Broker queue with the given name exists (sys.service_queues).
        public static async Task<bool> QueueExistsAsync(string connectionString, string queueName, CancellationToken cancellationToken)
            => await ScalarExistsAsync(connectionString,
                "SELECT 1 FROM sys.service_queues WHERE name = @name;",
                queueName, cancellationToken).ConfigureAwait(false);

        // True when a Service Broker service with the given name exists (sys.services).
        public static async Task<bool> ServiceExistsAsync(string connectionString, string serviceName, CancellationToken cancellationToken)
            => await ScalarExistsAsync(connectionString,
                "SELECT 1 FROM sys.services WHERE name = @name;",
                serviceName, cancellationToken).ConfigureAwait(false);

        // Reads sys.databases.is_broker_enabled for the named database over a 'master' (or any) connection. Used
        // by the broker-enable migration test to assert the production migration flips the broker bit 0 -> 1.
        public static async Task<bool> IsBrokerEnabledAsync(string masterConnectionString, string databaseName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT is_broker_enabled FROM sys.databases WHERE name = @name;";
            command.Parameters.Add(new SqlParameter("@name", databaseName));
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result != null && result != DBNull.Value && Convert.ToBoolean(result);
        }

        // Idempotently creates an empty database over a 'master' connection WITHOUT enabling Service Broker, so
        // the broker-enable migration test can exercise the production ENABLE_BROKER branch on a truly fresh DB.
        public static async Task CreateDatabaseAsync(string masterConnectionString, string databaseName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"IF DB_ID('{databaseName}') IS NULL CREATE DATABASE [{databaseName}];",
                cancellationToken).ConfigureAwait(false);
        }

        // Disables Service Broker on the named database over a 'master' connection with ROLLBACK IMMEDIATE. A
        // freshly CREATEd database inherits its broker state from the 'model' database, which on the test image
        // already has the broker enabled, so the broker-enable migration test must explicitly disable it first to
        // genuinely exercise the production ENABLE_BROKER branch (a 0 -> 1 flip).
        public static async Task DisableBrokerAsync(string masterConnectionString, string databaseName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Unconditional (not guarded by is_broker_enabled): a freshly created database's catalog state can
            // read stale for a brief window, so an is_broker_enabled = 1 guard could skip the disable and leave
            // the broker on. SET DISABLE_BROKER WITH ROLLBACK IMMEDIATE is idempotent and safe to run regardless.
            await ExecuteNonQueryAsync(connection,
                $"ALTER DATABASE [{databaseName}] SET DISABLE_BROKER WITH ROLLBACK IMMEDIATE;",
                cancellationToken).ConfigureAwait(false);
        }

        // Drops the named database over a 'master' connection, forcing SINGLE_USER WITH ROLLBACK IMMEDIATE first
        // so lingering connections cannot block the drop. Idempotent: a missing database is a no-op.
        public static async Task DropDatabaseAsync(string masterConnectionString, string databaseName, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"IF DB_ID('{databaseName}') IS NOT NULL " +
                "BEGIN " +
                $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{databaseName}]; " +
                "END;",
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> ScalarExistsAsync(string connectionString, string commandText, string name, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Parameters.Add(new SqlParameter("@name", name));
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result != null && result != DBNull.Value;
        }

        private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(new SqlParameter(name, value));
            }
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
