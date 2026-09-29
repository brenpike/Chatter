using System;
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.RabbitMq;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Collection fixture that brings up a RabbitMQ 4.3 container once per test collection, alongside the 3.13
    // container RabbitMqFixture starts. RabbitMQ 4.3 changed how a quorum queue counts a basic.nack with requeue,
    // which is what a quorum-queue Receiver relies on to advance its attempt count, so the delivery-counting
    // scenarios run against this broker version as well. Topology is declared in-test via RabbitMqTopology, exactly
    // as for the 3.13 fixture.
    //
    // When Docker is unavailable the fixture is a no-op: InitializeAsync starts nothing (the container stays null)
    // and GetAmqpConnectionString throws. The RequiresDockerFact attribute SKIPS the tests at discovery time in that
    // case, so a no-Docker `dotnet test` stays green and the fixture's throw is never reached.
    public sealed class RabbitMq43Fixture : IAsyncLifetime
    {
        private const string RabbitMqImage = "rabbitmq:4.3-management";

        // The container-internal management API port. Testcontainers maps this to a RANDOM host port (NOT 15672),
        // so the management base URI must be built from GetMappedPublicPort(15672) rather than a hardcoded port.
        private const int ManagementContainerPort = 15672;

        // Bounds container start so a wedged Docker pull/start fails fast instead of hanging the test collection.
        // Generous because a cold image pull is slow; still finite.
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

        private RabbitMqContainer _container;

        // The AMQP connection URI (amqp://user:pass@host:mappedPort) for the running container. Throws if the
        // container was not started (Docker unavailable).
        public string GetAmqpConnectionString()
        {
            if (_container is null)
            {
                throw new InvalidOperationException(
                    "The RabbitMQ 4.3 container was not started (Docker unavailable). " +
                    "Integration tests must be guarded with [RequiresDockerFact].");
            }

            return _container.GetConnectionString();
        }

        // The management HTTP API base URI (http://host:mappedManagementPort) for the running container, resolved
        // from the randomly mapped host port at call time. Throws if the container was not started (Docker
        // unavailable).
        public Uri GetManagementBaseUri()
        {
            if (_container is null)
            {
                throw new InvalidOperationException(
                    "The RabbitMQ 4.3 container was not started (Docker unavailable). " +
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

            // RabbitMqBuilder publishes only the AMQP port (5672) by default, so the management port (15672) is
            // published explicitly to a random host port; GetManagementBaseUri resolves that mapped port.
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

    // DisableParallelization keeps this collection from running concurrently with the 3.13 RabbitMqCollection (also
    // non-parallel), so the two brokers and their Receivers never compete for the host at the same time, and the
    // process-global ActivityListener hazard RabbitMqCollection guards against cannot leak across them.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class RabbitMq43Collection : ICollectionFixture<RabbitMq43Fixture>
    {
        public const string Name = "RabbitMq43";
    }
}
