using System;
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.RabbitMq;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Collection fixture that brings up a RabbitMQ container once per test collection. The production adapter
    // provisions NO topology (docs/design/rabbitmq-adapter.md §10), so the per-scenario queues/exchanges are
    // declared in-test via RabbitMqTopology against this container's AMQP endpoint before each scenario runs.
    //
    // When Docker is unavailable the fixture is a no-op: InitializeAsync starts nothing (the container stays
    // null) and GetAmqpConnectionString throws. The RequiresDockerFact attribute SKIPS the tests at discovery
    // time in that case, so a no-Docker `dotnet test` stays green and the fixture's throw is never reached.
    // Mirrors the SQL Service Broker / Azure Service Bus emulator fixtures.
    public sealed class RabbitMqFixture : IAsyncLifetime
    {
        // The RabbitMQ image is passed explicitly to RabbitMqBuilder's image ctor (the parameterless ctor is
        // obsolete and pins an old tag), mirroring how the SQL Service Broker fixture pins its image. A 3.13
        // image supports BOTH quorum queues (native x-delivery-count, introduced in 3.8) and classic queues,
        // which the deadletter scenario proves on both per ADR-0001.
        private const string RabbitMqImage = "rabbitmq:3.13-management";

        // The container-internal management API port. Testcontainers maps this to a RANDOM host port (NOT 15672),
        // so the management base URI must be built from GetMappedPublicPort(15672) rather than a hardcoded port.
        private const int ManagementContainerPort = 15672;

        // Bounds container start so a wedged Docker pull/start fails fast instead of hanging the test collection.
        // Generous because a cold image pull is slow; still finite.
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

        private RabbitMqContainer _container;

        // The AMQP connection URI (amqp://user:pass@host:mappedPort) for the running container. Throws if the
        // container was not started (Docker unavailable), mirroring the SQL Service Broker fixture's throw.
        public string GetAmqpConnectionString()
        {
            if (_container is null)
            {
                throw new InvalidOperationException(
                    "The RabbitMQ container was not started (Docker unavailable). " +
                    "Integration tests must be guarded with [RequiresDockerFact].");
            }

            return _container.GetConnectionString();
        }

        // The management HTTP API base URI (http://host:mappedManagementPort) for the running container. The
        // management port 15672 is mapped by Testcontainers to a RANDOM host port, so this resolves the mapped
        // port at call time rather than assuming the default. Throws if the container was not started (Docker
        // unavailable), mirroring GetAmqpConnectionString.
        public Uri GetManagementBaseUri()
        {
            if (_container is null)
            {
                throw new InvalidOperationException(
                    "The RabbitMQ container was not started (Docker unavailable). " +
                    "Integration tests must be guarded with [RequiresDockerFact].");
            }

            return new UriBuilder("http", _container.Hostname, _container.GetMappedPublicPort(ManagementContainerPort)).Uri;
        }

        public async Task InitializeAsync()
        {
            if (!DockerEnvironment.IsAvailable)
            {
                return;
            }

            using var startupCts = new CancellationTokenSource(StartupTimeout);

            // RabbitMqBuilder publishes only the AMQP port (5672) by default. The recovery-epoch test drops broker
            // connections via the management HTTP API, so the management port (15672) must also be published to a
            // random host port; GetManagementBaseUri resolves that mapped port at call time.
            _container = new RabbitMqBuilder(RabbitMqImage)
                .WithPortBinding(ManagementContainerPort, assignRandomHostPort: true)
                .Build();
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
    public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
    {
        public const string Name = "RabbitMq";
    }
}
