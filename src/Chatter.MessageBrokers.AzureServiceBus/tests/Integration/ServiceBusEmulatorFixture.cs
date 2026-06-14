using System;
using System.IO;
using System.Threading.Tasks;
using Testcontainers.ServiceBus;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Collection fixture that brings up the official Azure Service Bus emulator (plus its required MSSQL
    // sidecar, auto-wired by the Testcontainers ServiceBus module) once per test collection. The queues the
    // integration tests use are provisioned declaratively from Config.json (the emulator does NOT
    // auto-create entities), mapped into the container via WithConfig.
    //
    // When Docker is unavailable the fixture is a no-op: InitializeAsync starts nothing and
    // GetConnectionString throws. The RequiresDockerFact attribute SKIPS the tests at discovery time in
    // that case, so a no-Docker `dotnet test` stays green and the fixture's throw is never reached.
    public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
    {
        public const string QueueA = "queue.a";
        public const string QueueB = "queue.b";

        // The official Azure Service Bus emulator image. Passed explicitly to ServiceBusBuilder (the
        // parameterless ctor is obsolete); the emulator publishes :latest as its documented tag.
        private const string EmulatorImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";

        private ServiceBusContainer _container;

        public string GetConnectionString()
        {
            if (_container is null)
            {
                throw new InvalidOperationException(
                    "The Service Bus emulator was not started (Docker unavailable). " +
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

            var configPath = Path.Combine(AppContext.BaseDirectory, "Integration", "Config.json");

            _container = new ServiceBusBuilder(EmulatorImage)
                .WithAcceptLicenseAgreement(true)
                .WithConfig(configPath)
                .Build();

            await _container.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }
    }

    [CollectionDefinition(Name)]
    public sealed class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture>
    {
        public const string Name = "ServiceBusEmulator";
    }
}
