using System;
using System.IO;
using System.Threading;
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

        // INVARIANT: every name in LeasableQueues has a queue entry of the same name in Integration/Config.json.
        // The emulator provisions entities declaratively at container start and rejects receivers for entities
        // it was never told about.
        private static readonly string[] LeasableQueues =
        {
            "chatter.leased.00",
            "chatter.leased.01",
            "chatter.leased.02",
            "chatter.leased.03",
            "chatter.leased.04",
            "chatter.leased.05",
            "chatter.leased.06",
            "chatter.leased.07",
        };

        private static int _leasedQueueIndex = -1;

        // The official Azure Service Bus emulator image. Passed explicitly to ServiceBusBuilder (the
        // parameterless ctor is obsolete); the emulator publishes :latest as its documented tag.
        private const string EmulatorImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";

        private ServiceBusContainer _container;

        // Hands out a pooled queue name no other caller has taken, so no two tests share an entity on the
        // long-lived emulator. Pure string allocation: it touches neither the container nor the emulator, so it
        // still works when Docker is absent and InitializeAsync started nothing.
        public string LeaseQueue()
        {
            var leasedIndex = Interlocked.Increment(ref _leasedQueueIndex);

            if (leasedIndex >= LeasableQueues.Length)
            {
                throw new InvalidOperationException(
                    $"The integration queue pool is exhausted; all {LeasableQueues.Length} leasable queues are " +
                    "taken. Add another name to LeasableQueues in Integration/ServiceBusEmulatorFixture.cs AND a " +
                    "matching queue entry with that exact name to Integration/Config.json. Both files must " +
                    "declare the same queue names.");
            }

            return LeasableQueues[leasedIndex];
        }

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
