using System;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.MsSql;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Collection fixture that brings up a SQL Server container once per test collection for the SqlChangeFeed
    // end-to-end integration suite, and creates ONE dedicated application database with Service Broker ALREADY
    // enabled. It deliberately provisions NO broker objects, table, or trigger: the production change-feed
    // migration (UseChangeFeedSqlMigrations<TRow>) is the SYSTEM UNDER TEST and installs all of those itself.
    //
    // Why the app database is created with the broker already enabled: it gives the shared CRUD / re-arm /
    // uninstall test classes a known starting state in which the production install script's broker-enable branch
    // is a no-op (its `is_broker_enabled = 0` guard skips it), so those classes exercise only the object-creation
    // path. The dedicated tests that must exercise the ENABLE_BROKER branch itself
    // (ChangeFeedBrokerEnableMigrationTests, ChangeFeedDatabaseOwnershipTests) create their OWN fresh databases
    // with the broker explicitly disabled.
    //
    // When Docker is unavailable the fixture is a no-op: InitializeAsync starts nothing (the container stays
    // null) and the connection-string getters throw. RequiresDockerFact SKIPS the tests at discovery time in
    // that case, so a no-Docker `dotnet test` stays green and the throw is never reached. Mirrors the SQL
    // Service Broker integration fixture.
    public sealed class SqlChangeFeedFixture : IAsyncLifetime
    {
        // The SQL Server image is passed explicitly to MsSqlBuilder (the parameterless ctor is obsolete),
        // matching the SQL Service Broker integration fixture. Supports ENABLE_BROKER and the Service Broker
        // objects the production migration installs.
        private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

        // The dedicated application database the change-feed migration runs against for the CRUD / re-arm /
        // uninstall test classes. Kept distinct from any database the container ships with so teardown can drop
        // it wholesale. The broker-enable and database-ownership tests create their own separate databases instead.
        public const string DatabaseName = "chatter_scf_it";

        // Bounds container start + database creation so a wedged Docker pull/start or a hung setup DDL fails fast
        // instead of hanging the test collection. Generous because a cold image pull is slow; still finite.
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

        // Bounds the best-effort teardown so a hung database drop cannot block container disposal indefinitely.
        private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

        // Bounded readiness retry. A freshly started SQL Server container can refuse connections for a brief
        // window; these caps keep the connect + database-create retry short and finite.
        private const int MaxConnectAttempts = 10;
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);

        private MsSqlContainer _container;

        // The master connection string for the running container. Throws if the container was not started
        // (Docker unavailable), mirroring the SQL Service Broker fixture's throw.
        public string GetMasterConnectionString()
        {
            if (_container is null)
            {
                throw new InvalidOperationException(
                    "The SQL Server container was not started (Docker unavailable). " +
                    "Integration tests must be guarded with [RequiresDockerFact].");
            }

            return _container.GetConnectionString();
        }

        // The connection string pointed at the dedicated application database, derived from the master
        // connection string by repointing InitialCatalog so every other setting (credentials, encryption,
        // trust flags) the container configured is preserved. Throws if the container was not started.
        public string GetAppConnectionString()
            => RepointCatalog(GetMasterConnectionString(), DatabaseName);

        // The connection string pointed at an arbitrary database on the same container, preserving every other
        // setting. The broker-enable and database-ownership tests use this to reach the fresh databases they
        // create without a pre-enabled broker. Throws if the container was not started.
        public string GetConnectionStringForDatabase(string databaseName)
            => RepointCatalog(GetMasterConnectionString(), databaseName);

        public async Task InitializeAsync()
        {
            if (!DockerEnvironment.IsAvailable)
            {
                return;
            }

            using var startupCts = new CancellationTokenSource(StartupTimeout);

            _container = new MsSqlBuilder(SqlServerImage).Build();
            await _container.StartAsync(startupCts.Token).ConfigureAwait(false);

            await EnsureAppDatabaseWithBrokerEnabledAsync(_container.GetConnectionString(), startupCts.Token)
                .ConfigureAwait(false);
        }

        public async Task DisposeAsync()
        {
            if (_container is null)
            {
                return;
            }

            try
            {
                using var teardownCts = new CancellationTokenSource(TeardownTimeout);
                await DropDatabaseAsync(_container.GetConnectionString(), DatabaseName, teardownCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort teardown: the container is disposed below regardless, so a database-drop failure
                // (or a teardown that exceeded TeardownTimeout) must not mask container disposal.
            }

            await _container.DisposeAsync().ConfigureAwait(false);
        }

        // Connects to 'master', creates the dedicated app database if absent, and enables Service Broker on it so
        // the broker is on before any test runs, making the production migration's ENABLE_BROKER branch a no-op for
        // the classes that use this database. Wrapped in a bounded retry so a not-yet-ready container surfaces as a
        // retry rather than a hard failure.
        private static async Task EnsureAppDatabaseWithBrokerEnabledAsync(string masterConnectionString, CancellationToken cancellationToken)
        {
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    await using var connection = new SqlConnection(masterConnectionString);
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                    await ExecuteNonQueryAsync(connection,
                        $"IF DB_ID('{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];",
                        cancellationToken).ConfigureAwait(false);

                    await ExecuteNonQueryAsync(connection,
                        "IF NOT EXISTS (SELECT 1 FROM sys.databases " +
                        $"WHERE name = '{DatabaseName}' AND is_broker_enabled = 1) " +
                        $"ALTER DATABASE [{DatabaseName}] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;",
                        cancellationToken).ConfigureAwait(false);

                    return;
                }
                catch (SqlException) when (attempt < MaxConnectAttempts)
                {
                    await Task.Delay(ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Drops the named database, forcing SINGLE_USER WITH ROLLBACK IMMEDIATE first so lingering harness or
        // receiver connections cannot block it. Idempotent: a missing database is a no-op.
        private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName, CancellationToken cancellationToken)
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

        // Returns the supplied connection string repointed at the named database, preserving every other setting
        // (credentials, encryption, trust flags) the container configured.
        private static string RepointCatalog(string connectionString, string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = databaseName,
            };
            return builder.ConnectionString;
        }

        private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [CollectionDefinition(Name)]
    public sealed class SqlChangeFeedCollection : ICollectionFixture<SqlChangeFeedFixture>
    {
        public const string Name = "SqlChangeFeed";
    }
}
