using System;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.MsSql;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Collection fixture that brings up a SQL Server container once per test collection and provisions the
    // Service Broker objects (database, message type, contract, queues, services) the SSB integration harness
    // sends/receives over, via ServiceBrokerProvisioning. The provisioned object names are the production-pinned
    // and harness-chosen constants on ServiceBrokerProvisioning, which the ChatterSsbPipelineHarness wires into
    // ReceiverOptions and the stamped SSBMessageContext headers.
    //
    // When Docker is unavailable the fixture is a no-op: InitializeAsync starts nothing (the container stays
    // null) and GetAppConnectionString throws. The RequiresDockerFact attribute SKIPS the tests at discovery
    // time in that case, so a no-Docker `dotnet test` stays green and the fixture's throw is never reached.
    // This mirrors the Azure Service Bus emulator fixture.
    public sealed class SqlServiceBrokerFixture : IAsyncLifetime
    {
        // The SQL Server image is passed explicitly to MsSqlBuilder (the parameterless ctor is obsolete),
        // mirroring how the Azure Service Bus emulator fixture pins its image. This is the Testcontainers.MsSql
        // module's documented default image and supports the ENABLE_BROKER / Service Broker objects the harness
        // provisions.
        private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

        // Bounds container start + provisioning so a wedged Docker pull/start or a hung provisioning DDL fails
        // fast instead of hanging the test collection. Generous because a cold image pull is slow; still finite.
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

        // Bounds the best-effort teardown so a hung object drop cannot block container disposal indefinitely.
        private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

        private MsSqlContainer _container;

        // The master connection string for the running container. Throws if the container was not started
        // (Docker unavailable), mirroring the Azure Service Bus fixture's GetConnectionString throw.
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

        // The connection string pointed at the provisioned application database (ServiceBrokerProvisioning
        // .DatabaseName). Derived from the master connection string by repointing InitialCatalog so every other
        // setting (credentials, encryption, trust flags) the container configured is preserved. Throws if the
        // container was not started.
        public string GetAppConnectionString()
        {
            var builder = new SqlConnectionStringBuilder(GetMasterConnectionString())
            {
                InitialCatalog = ServiceBrokerProvisioning.DatabaseName,
            };
            return builder.ConnectionString;
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

            await ServiceBrokerProvisioning
                .SetupAsync(_container.GetConnectionString(), startupCts.Token)
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
                await ServiceBrokerProvisioning
                    .TeardownAsync(_container.GetConnectionString(), teardownCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort teardown: the container is disposed below regardless, so a provisioning-object
                // drop failure (or a teardown that exceeded TeardownTimeout) must not mask container disposal.
            }

            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    [CollectionDefinition(Name)]
    public sealed class SqlServiceBrokerCollection : ICollectionFixture<SqlServiceBrokerFixture>
    {
        public const string Name = "SqlServiceBroker";
    }
}
