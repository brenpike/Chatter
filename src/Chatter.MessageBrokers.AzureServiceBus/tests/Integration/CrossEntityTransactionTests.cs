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

        // Assertion-only read helper: callers only assert on the returned message and never settle it.
        // Uses ReceiveAndDelete (NOT PeekLock) so the message is removed on receipt and cannot reappear
        // once its PeekLock lock would expire. The emulator fixture reuses the same queue across test
        // methods with a 10-second lock duration, so a peeked-but-unsettled message would otherwise become
        // visible again and be consumed by a later test, causing order/timing-dependent cross-test leakage.
        private static async Task<ServiceBusReceivedMessage> ReceiveForAssertionAsync(ServiceBusClient client, string queue)
        {
            var receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
            });
            return await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        }

        // atomic-commit: receive from A and send to B inside ONE TransactionScope, then Complete the scope.
        // Asserts A's message is consumed AND B receives the message — the happy-path cross-entity guarantee.
        //
        // SKIPPED, NOT FAILED: the Azure Service Bus emulator does not support cross-entity
        // (multi-top-level-entity) transactions — it throws "Local transactions cannot span multiple top-level
        // entities". This is the exact CI integration-job failure that proved the limitation. Cross-entity
        // transactions ARE a real Azure Service Bus feature, so this test body stays intact and ready-to-run;
        // only execution is gated. Verify FullAtomicityViaInfrastructure against a real Azure Service Bus
        // namespace. Tracked for real-namespace CI; remove the Skip to enable once running against a real
        // namespace.
        [Fact(Skip = "Azure Service Bus emulator does not support cross-entity (multi-top-level-entity) " +
                     "transactions — 'Local transactions cannot span multiple top-level entities'. Verify " +
                     "FullAtomicityViaInfrastructure against a real Azure Service Bus namespace. Tracked for " +
                     "real-namespace CI.")]
        [Trait("Category", "Integration")]
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

            var delivered = await ReceiveForAssertionAsync(client, ServiceBusEmulatorFixture.QueueB);
            delivered.Should().NotBeNull("the forwarded message must be delivered to queue B after the scope commits");
            delivered.Body.ToString().Should().Be("forwarded-commit");

            // Source message was completed inside the committed scope, so A holds nothing more.
            var leftoverOnA = await ReceiveForAssertionAsync(client, ServiceBusEmulatorFixture.QueueA);
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

            var delivered = await ReceiveForAssertionAsync(client, ServiceBusEmulatorFixture.QueueB);
            delivered.Should().NotBeNull("a send outside any cross-entity scope must still deliver");
            delivered.Body.ToString().Should().Be("non-atomic");
        }

        // single-entity atomic-commit: receive a seed message from queue B and send a follow-up to the SAME
        // queue B inside ONE TransactionScope, then Complete the scope. Because both operations target a single
        // top-level entity this stays within what the Azure Service Bus emulator supports (it rejects only
        // multi-top-level-entity / cross-entity transactions), so it proves the TransactionScope enlistment
        // wiring works on the emulator for the supported single-entity case. Asserts the seed is consumed AND
        // the follow-up is delivered to B after the scope commits.
        [RequiresDockerFact]
        public async Task CommittedSingleEntityTransactionConsumesSourceAndDeliversToSameEntity()
        {
            var client = CreateSharedCrossEntityClient();
            await SeedAsync(client, ServiceBusEmulatorFixture.QueueB, "seed-single-entity");

            var receiver = client.CreateReceiver(ServiceBusEmulatorFixture.QueueB, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });
            var sender = client.CreateSender(ServiceBusEmulatorFixture.QueueB);

            var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            received.Should().NotBeNull("the seed message must be available on queue B");
            received.Body.ToString().Should().Be("seed-single-entity");

            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await sender.SendMessageAsync(new ServiceBusMessage("forwarded-single-entity"));
                await receiver.CompleteMessageAsync(received);
                scope.Complete();
            }

            // The seed was completed and the follow-up was sent in one committed scope, so the only message
            // left on B is the follow-up.
            var delivered = await ReceiveForAssertionAsync(client, ServiceBusEmulatorFixture.QueueB);
            delivered.Should().NotBeNull("the follow-up message must be delivered to queue B after the scope commits");
            delivered.Body.ToString().Should().Be("forwarded-single-entity");
        }

        // atomic-rollback (cross-entity): receive from A and send to B inside one TransactionScope, then throw
        // BEFORE scope.Complete() → B must NOT receive the message AND A's message must be redelivered (the
        // PeekLock is released because CompleteMessageAsync never committed).
        //
        // SKIPPED, NOT FAILED: the Azure Service Bus emulator does not support cross-entity
        // (multi-top-level-entity) transactions — it throws "Local transactions cannot span multiple top-level
        // entities". This is the exact CI integration-job failure that proved the limitation. Cross-entity
        // transactions ARE a real Azure Service Bus feature, so this test body stays intact and ready-to-run;
        // only execution is gated. Verify FullAtomicityViaInfrastructure against a real Azure Service Bus
        // namespace. Tracked for real-namespace CI; remove the Skip to enable once running against a real
        // namespace.
        [Fact(Skip = "Azure Service Bus emulator does not support cross-entity (multi-top-level-entity) " +
                     "transactions — 'Local transactions cannot span multiple top-level entities'. Verify " +
                     "FullAtomicityViaInfrastructure against a real Azure Service Bus namespace. Tracked for " +
                     "real-namespace CI.")]
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

            var deliveredToB = await ReceiveForAssertionAsync(client, ServiceBusEmulatorFixture.QueueB);
            deliveredToB.Should().BeNull("the forwarded send must roll back when the scope does not complete");

            var redeliveredOnA = await ReceiveForAssertionAsync(client, ServiceBusEmulatorFixture.QueueA);
            redeliveredOnA.Should().NotBeNull("the source message must be redelivered when the settle rolls back");
        }
    }
}
