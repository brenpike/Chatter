using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.Testing.Core.Integration;
using Testcontainers.CosmosDb;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // Collection fixture that brings up the Cosmos DB LINUX vnext-preview emulator once per test collection via the
    // Testcontainers Cosmos module. The classic Windows/linux emulator is heavy and flaky; the vnext-preview linux
    // emulator is the supported target for the document-tier reliability integration suite (#223). The image is pinned
    // EXPLICITLY on the builder rather than relying on the module's default tag, mirroring how the RabbitMQ / Azure
    // Service Bus fixtures pin their images.
    //
    // When Docker is unavailable the fixture is a no-op: InitializeAsync starts nothing (the container stays null) and
    // the accessors throw. The RequiresDockerFact attribute SKIPS the tests at discovery time in that case, so a
    // no-Docker `dotnet test` stays green and the fixture's throw is never reached. Mirrors RabbitMqFixture /
    // ServiceBusEmulatorFixture.
    public sealed class CosmosEmulatorFixture : IAsyncLifetime
    {
        // The Cosmos linux vnext-preview emulator image. Pinned explicitly to the builder (the module default tag
        // happens to match today, but pinning keeps the suite deterministic if the module default ever advances).
        private const string EmulatorImage = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";

        // The well-known Cosmos emulator master key (constant across every emulator instance). Exposed so the test
        // CosmosClient builder can authenticate without parsing it out of the connection string.
        public const string WellKnownEmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        // Bounds container start so a wedged Docker pull/start fails fast instead of hanging the collection. The Cosmos
        // emulator cold start (image pull + emulator boot) is slow — minutes — so the budget is generous, but still
        // finite. Passed to StartAsync's cancellation token, mirroring RabbitMqFixture (the Testcontainers builder
        // exposes no WithStartupTimeout; the start budget is the StartAsync token).
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(10);

        private CosmosDbContainer _container;

        // The emulator gateway endpoint (https://host:mappedPort/) for the running container, parsed from the
        // emulator's connection string. Throws if the container was not started (Docker unavailable).
        public string GetEmulatorEndpoint()
        {
            string connectionString = GetConnectionString();
            foreach (string segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                const string accountEndpointPrefix = "AccountEndpoint=";
                if (segment.StartsWith(accountEndpointPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return segment.Substring(accountEndpointPrefix.Length);
                }
            }

            throw new InvalidOperationException(
                "The Cosmos emulator connection string did not contain an 'AccountEndpoint=' segment.");
        }

        // The raw emulator connection string (AccountEndpoint=...;AccountKey=...). Throws if the container was not
        // started (Docker unavailable), mirroring the RabbitMQ / Service Bus fixtures.
        public string GetConnectionString()
        {
            if (_container is null)
            {
                throw new InvalidOperationException(
                    "The Cosmos emulator container was not started (Docker unavailable). " +
                    "Integration tests must be guarded with [RequiresDockerFact].");
            }

            return _container.GetConnectionString();
        }

        public async Task InitializeAsync()
        {
            if (!DockerEnvironment.IsAvailable)
            {
                return;
            }

            using var startupCts = new CancellationTokenSource(StartupTimeout);

            _container = new CosmosDbBuilder(EmulatorImage).Build();
            await _container.StartAsync(startupCts.Token).ConfigureAwait(false);
        }

        public async Task DisposeAsync()
        {
            if (_container is null)
            {
                return;
            }

            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    [CollectionDefinition(Name)]
    public sealed class CosmosEmulatorCollection : ICollectionFixture<CosmosEmulatorFixture>
    {
        public const string Name = "CosmosEmulator";
    }
}
