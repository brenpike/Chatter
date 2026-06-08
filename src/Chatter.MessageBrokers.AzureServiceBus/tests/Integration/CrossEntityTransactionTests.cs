using System;
using System.Threading.Tasks;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Broker-level proof of the cross-entity transaction guarantee that Chatter's FullAtomicityViaInfrastructure
    // mode relies on. These tests drive the Azure.Messaging.ServiceBus SDK directly off a single
    // EnableCrossEntityTransactions ServiceBusClient — the exact client shape Chatter builds in
    // ChatterAzureServiceBusExtensions.CreateSharedClient (one client per namespace, cross-entity enabled).
    // Exercising the SDK directly isolates the broker guarantee (receive-settle + send enlisting in one
    // TransactionScope) from the Chatter pipeline's scope orchestration, which is unit-tested elsewhere.
    //
    // All Docker-gated facts are SKIPPED (never failed) when Docker is absent so a plain `dotnet test`
    // stays green; see RequiresDockerFactAttribute and ServiceBusEmulatorFixture.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class CrossEntityTransactionTests
    {
        private readonly ServiceBusEmulatorFixture _emulator;

        public CrossEntityTransactionTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        // Mirrors ChatterAzureServiceBusExtensions.CreateSharedClient for the SAS (connection-string) path:
        // a single client per namespace with EnableCrossEntityTransactions so a send and a receive-settle on
        // different entities enlist in one transaction.
        private ServiceBusClient CreateSharedCrossEntityClient()
            => new ServiceBusClient(
                _emulator.GetConnectionString(),
                new ServiceBusClientOptions { EnableCrossEntityTransactions = true });

        private static async Task SeedAsync(ServiceBusClient client, string queue, string body)
        {
            var sender = client.CreateSender(queue);
            await sender.SendMessageAsync(new ServiceBusMessage(body));
        }

        private static async Task<ServiceBusReceivedMessage> PeekLockReceiveAsync(ServiceBusClient client, string queue)
        {
            var receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });
            return await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        }

        // atomic-commit: receive from A and send to B inside ONE TransactionScope, then Complete the scope.
        // Asserts A's message is consumed AND B receives the message — the happy-path cross-entity guarantee.
        [RequiresDockerFact]
        public async Task CommittedCrossEntityTransactionConsumesSourceAndDeliversToDestination()
        {
            var client = CreateSharedCrossEntityClient();
            await SeedAsync(client, ServiceBusEmulatorFixture.QueueA, "seed-commit");

            var receiver = client.CreateReceiver(ServiceBusEmulatorFixture.QueueA, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });
            var sender = client.CreateSender(ServiceBusEmulatorFixture.QueueB);

            var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            received.Should().NotBeNull("the seed message must be available on queue A");

            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await sender.SendMessageAsync(new ServiceBusMessage("forwarded-commit"));
                await receiver.CompleteMessageAsync(received);
                scope.Complete();
            }

            var delivered = await PeekLockReceiveAsync(client, ServiceBusEmulatorFixture.QueueB);
            delivered.Should().NotBeNull("the forwarded message must be delivered to queue B after the scope commits");
            delivered.Body.ToString().Should().Be("forwarded-commit");

            // Source message was completed inside the committed scope, so A holds nothing more.
            var leftoverOnA = await PeekLockReceiveAsync(client, ServiceBusEmulatorFixture.QueueA);
            leftoverOnA.Should().BeNull("the source message must be consumed when the scope commits");
        }

        // non-atomic regression: a plain ReceiveAndDelete-style send WITHOUT any cross-entity TransactionScope
        // still delivers to the destination. Mirrors Chatter's TransactionMode.None / ReceiveOnly path where
        // the sender opens no atomic scope.
        [RequiresDockerFact]
        public async Task NonAtomicSendWithoutCrossEntityScopeStillDelivers()
        {
            var client = CreateSharedCrossEntityClient();

            var sender = client.CreateSender(ServiceBusEmulatorFixture.QueueB);
            await sender.SendMessageAsync(new ServiceBusMessage("non-atomic"));

            var delivered = await PeekLockReceiveAsync(client, ServiceBusEmulatorFixture.QueueB);
            delivered.Should().NotBeNull("a send outside any cross-entity scope must still deliver");
            delivered.Body.ToString().Should().Be("non-atomic");
        }

        // atomic-rollback (cross-entity): receive from A and send to B inside one TransactionScope, then throw
        // BEFORE scope.Complete() → B must NOT receive the message AND A's message must be redelivered (the
        // PeekLock is released because CompleteMessageAsync never committed).
        //
        // SKIPPED, NOT FAILED: the Azure Service Bus emulator's support for cross-entity transactional
        // rollback is unverified — it could not be exercised in the authoring environment (Docker
        // unavailable), and the emulator is documented as a development/test tool with no SLA and known
        // feature gaps. Faking a pass here would be dishonest, so this test is authored ready-to-run but
        // skipped with this documented reason. Flagged to the overlord as a possible blocker (cross-entity
        // rollback support was called out in planning). Remove the Skip to enable once the emulator is
        // confirmed to honor cross-entity transactional rollback.
        [Fact(Skip = "Cross-entity transactional ROLLBACK against the Azure Service Bus emulator is unverified " +
                     "(emulator support for cross-entity transactions could not be confirmed in the authoring " +
                     "environment). Authored ready-to-run; remove Skip once the emulator is confirmed to honor it.")]
        [Trait("Category", "Integration")]
        public async Task RolledBackCrossEntityTransactionDeliversNothingAndRedeliversSource()
        {
            var client = CreateSharedCrossEntityClient();
            await SeedAsync(client, ServiceBusEmulatorFixture.QueueA, "seed-rollback");

            var receiver = client.CreateReceiver(ServiceBusEmulatorFixture.QueueA, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });
            var sender = client.CreateSender(ServiceBusEmulatorFixture.QueueB);

            var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            received.Should().NotBeNull("the seed message must be available on queue A");

            try
            {
                using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
                await sender.SendMessageAsync(new ServiceBusMessage("forwarded-rollback"));
                await receiver.CompleteMessageAsync(received);
                throw new InvalidOperationException("force rollback before scope.Complete()");
                // scope.Complete() intentionally never reached.
            }
            catch (InvalidOperationException)
            {
                // expected: the scope disposes without Complete, rolling back the send and the settle.
            }

            // Abandon to release the lock immediately rather than waiting out LockDuration.
            await receiver.AbandonMessageAsync(received);

            var deliveredToB = await PeekLockReceiveAsync(client, ServiceBusEmulatorFixture.QueueB);
            deliveredToB.Should().BeNull("the forwarded send must roll back when the scope does not complete");

            var redeliveredOnA = await PeekLockReceiveAsync(client, ServiceBusEmulatorFixture.QueueA);
            redeliveredOnA.Should().NotBeNull("the source message must be redelivered when the settle rolls back");
        }
    }
}
