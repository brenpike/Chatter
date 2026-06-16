using System;
using Microsoft.Data.SqlClient;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.MsSql;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Integration
{
    // Collection fixture that brings up a SQL Server container once per test collection for the EntityFramework
    // reliability (outbox/inbox) integration suite. It provisions NO schema: each test class repoints to a
    // uniquely-named database on the shared container and the SqlServerOutboxContextHarness EnsureCreated()s the
    // OutboxMessage/InboxMessage tables there. Per-class DB isolation prevents cross-test row bleed on the single
    // OutboxMessage/InboxMessage tables.
    //
    // When Docker is unavailable the fixture is a no-op: InitializeAsync starts nothing (the container stays null)
    // and the connection-string accessors throw. The RequiresDockerFact attribute SKIPS the tests at discovery
    // time in that case, so a no-Docker `dotnet test` stays green and the fixture's throw is never reached. This
    // mirrors the SQL Service Broker / SqlChangeFeed integration fixtures.
    public sealed class EfReliabilitySqlServerFixture : IAsyncLifetime
    {
        // The SQL Server image is passed explicitly to MsSqlBuilder (the parameterless ctor is obsolete), mirroring
        // the SQL Service Broker / SqlChangeFeed integration fixtures.
        private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

        // Bounds container start so a wedged Docker pull/start fails fast instead of hanging the test collection.
        // Generous because a cold image pull is slow; still finite.
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

        // Bounds the best-effort teardown so a hung database drop cannot block container disposal indefinitely.
        private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

        // Bounded readiness retry. A freshly started SQL Server container can refuse connections for a brief window;
        // these caps keep the connect + database-create retry short and finite.
        private const int MaxConnectAttempts = 10;
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);

        private MsSqlContainer _container;

        // Tracks the per-class databases created on the shared container so teardown can drop each one.
        private readonly List<string> _createdDatabases = new List<string>();

        // The master connection string for the running container. Throws if the container was not started (Docker
        // unavailable), mirroring the sibling SQL fixtures' throw.
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

        // Creates a uniquely-named database on the shared container and returns a connection string pointed at it.
        // Each test class calls this once so it gets an isolated OutboxMessage/InboxMessage table set, preventing
        // cross-test row bleed. The InitialCatalog is repointed off the master connection string so every other
        // setting (credentials, encryption, trust flags) the container configured is preserved. Throws if the
        // container was not started.
        public async Task<string> CreateDatabaseAsync(string databasePrefix, CancellationToken cancellationToken = default)
        {
            var masterConnectionString = GetMasterConnectionString();
            var databaseName = $"{databasePrefix}_{Guid.NewGuid():N}";

            await CreateDatabaseWithRetryAsync(masterConnectionString, databaseName, cancellationToken).ConfigureAwait(false);
            _createdDatabases.Add(databaseName);

            return RepointCatalog(masterConnectionString, databaseName);
        }

        public async Task InitializeAsync()
        {
            if (!DockerEnvironment.IsAvailable)
            {
                return;
            }

            using var startupCts = new CancellationTokenSource(StartupTimeout);

            _container = new MsSqlBuilder(SqlServerImage).Build();
            await _container.StartAsync(startupCts.Token).ConfigureAwait(false);
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
                var masterConnectionString = _container.GetConnectionString();
                foreach (var databaseName in _createdDatabases)
                {
                    await DropDatabaseAsync(masterConnectionString, databaseName, teardownCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best-effort teardown: the container is disposed below regardless, so a database-drop failure (or a
                // teardown that exceeded TeardownTimeout) must not mask container disposal.
            }

            await _container.DisposeAsync().ConfigureAwait(false);
        }

        // Connects to 'master' and creates the named database. Wrapped in a bounded retry so a not-yet-ready
        // container surfaces as a retry rather than a hard failure.
        private static async Task CreateDatabaseWithRetryAsync(string masterConnectionString, string databaseName, CancellationToken cancellationToken)
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
                        $"IF DB_ID('{databaseName}') IS NULL CREATE DATABASE [{databaseName}];",
                        cancellationToken).ConfigureAwait(false);

                    return;
                }
                catch (SqlException) when (attempt < MaxConnectAttempts)
                {
                    await Task.Delay(ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Drops the named database, forcing SINGLE_USER WITH ROLLBACK IMMEDIATE first so lingering context
        // connections cannot block it. Idempotent: a missing database is a no-op.
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
    public sealed class EfReliabilitySqlServerCollection : ICollectionFixture<EfReliabilitySqlServerFixture>
    {
        public const string Name = "EfReliabilitySqlServer";
    }
}
