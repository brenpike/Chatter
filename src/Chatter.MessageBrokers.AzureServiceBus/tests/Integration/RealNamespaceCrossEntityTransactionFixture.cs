using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus.Administration;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Collection fixture for the real-namespace cross-entity transaction tests. Parallel to
    // ServiceBusEmulatorFixture, but provisions against a REAL Azure Service Bus namespace because the
    // emulator cannot exercise cross-entity (multi-top-level-entity) transactions.
    //
    // When a real-namespace connection string is configured (RealNamespaceEnvironment.IsConfigured) the
    // fixture creates two queues with UNIQUE per-run names via ServiceBusAdministrationClient (which ships
    // inside Azure.Messaging.ServiceBus, no extra package), mirroring Config.json's queue properties, and
    // deletes them on dispose (best-effort). When NOT configured the fixture is a no-op: InitializeAsync
    // starts nothing and GetConnectionString throws — but that throw is never reached because
    // RequiresRealServiceBusNamespaceFact skips the tests at discovery time first.
    public sealed class RealNamespaceCrossEntityTransactionFixture : IAsyncLifetime
    {
        // Match Config.json's emulator queue properties so the real-namespace queues behave identically.
        private static readonly TimeSpan QueueLockDuration = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan QueueDefaultTimeToLive = TimeSpan.FromHours(1);
        private const int QueueMaxDeliveryCount = 10;

        private ServiceBusAdministrationClient _adminClient;

        // Unique per-run queue names so concurrent or repeated runs never collide on a shared namespace.
        public string QueueA { get; } = $"chatter-xentity-a-{Guid.NewGuid():N}";
        public string QueueB { get; } = $"chatter-xentity-b-{Guid.NewGuid():N}";

        public string GetConnectionString()
        {
            if (_adminClient is null)
            {
                throw new InvalidOperationException(
                    "No real Azure Service Bus namespace was configured (" +
                    RealNamespaceEnvironment.ConnectionStringVariable + " unset). Real-namespace tests must " +
                    "be guarded with [RequiresRealServiceBusNamespaceFact].");
            }

            return RealNamespaceEnvironment.ConnectionString;
        }

        public async Task InitializeAsync()
        {
            if (!RealNamespaceEnvironment.IsConfigured)
            {
                return;
            }

            _adminClient = new ServiceBusAdministrationClient(RealNamespaceEnvironment.ConnectionString);

            await CreateQueueAsync(QueueA);
            await CreateQueueAsync(QueueB);
        }

        public async Task DisposeAsync()
        {
            if (_adminClient is null)
            {
                return;
            }

            await DeleteQueueBestEffortAsync(QueueA);
            await DeleteQueueBestEffortAsync(QueueB);
        }

        private async Task CreateQueueAsync(string queueName)
        {
            var options = new CreateQueueOptions(queueName)
            {
                LockDuration = QueueLockDuration,
                DefaultMessageTimeToLive = QueueDefaultTimeToLive,
                MaxDeliveryCount = QueueMaxDeliveryCount,
                DeadLetteringOnMessageExpiration = false,
                RequiresDuplicateDetection = false,
                RequiresSession = false,
            };

            await _adminClient.CreateQueueAsync(options);
        }

        // Best-effort cleanup: a queue already gone (manual deletion, prior failed run) must not fail dispose.
        private async Task DeleteQueueBestEffortAsync(string queueName)
        {
            try
            {
                await _adminClient.DeleteQueueAsync(queueName);
            }
            catch (Azure.Messaging.ServiceBus.ServiceBusException ex)
                when (ex.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
            {
                // already gone — nothing to clean up.
            }
        }
    }

    [CollectionDefinition(Name)]
    public sealed class RealNamespaceCrossEntityTransactionCollection
        : ICollectionFixture<RealNamespaceCrossEntityTransactionFixture>
    {
        public const string Name = "RealNamespaceCrossEntityTransaction";
    }
}
