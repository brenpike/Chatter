using System;
using System.Threading.Tasks;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Real-namespace proof of the cross-entity transaction guarantee that Chatter's
    // FullAtomicityViaInfrastructure mode relies on. The Azure Service Bus emulator CANNOT exercise these —
    // cross-entity (multi-top-level-entity) transactions throw "Local transactions cannot span multiple
    // top-level entities" on the emulator — so these tests target a REAL Azure Service Bus namespace.
    //
    // They drive the Azure.Messaging.ServiceBus SDK directly off a single EnableCrossEntityTransactions
    // ServiceBusClient — the exact client shape Chatter builds in ChatterAzureServiceBusExtensions
    // .CreateSharedClient (one client per namespace, cross-entity enabled). Exercising the SDK directly
    // isolates the broker guarantee (receive-settle + send enlisting in one TransactionScope) from the
    // Chatter pipeline's scope orchestration, which is unit-tested elsewhere.
    //
    // CRITICAL — TRAIT: this class carries ONLY [Trait("Category","RealNamespaceIntegration")] and NOT the
    // Integration trait. xUnit traits are additive, so an Integration trait here would let the emulator CI
    // lane (`--filter Category=Integration`) re-select these and fail on the emulator. They run only in the
    // dedicated real-namespace CI lane and locally when CHATTER_ASB_REAL_NAMESPACE_CONNECTION_STRING is set.
    //
    // All facts are gated by [RequiresRealServiceBusNamespaceFact] and are SKIPPED (never failed) when no
    // real-namespace connection string is configured, so a plain `dotnet test` stays green.
    [Trait("Category", "RealNamespaceIntegration")]
    [Collection(RealNamespaceCrossEntityTransactionCollection.Name)]
    public class RealNamespaceCrossEntityTransactionTests
    {
        private readonly RealNamespaceCrossEntityTransactionFixture _namespace;

        public RealNamespaceCrossEntityTransactionTests(RealNamespaceCrossEntityTransactionFixture @namespace)
            => _namespace = @namespace;

        // Mirrors ChatterAzureServiceBusExtensions.CreateSharedClient for the SAS (connection-string) path:
        // a single client per namespace with EnableCrossEntityTransactions so a send and a receive-settle on
        // different entities enlist in one transaction.
        private ServiceBusClient CreateSharedCrossEntityClient()
            => new ServiceBusClient(
                _namespace.GetConnectionString(),
                new ServiceBusClientOptions { EnableCrossEntityTransactions = true });

        private static async Task SeedAsync(ServiceBusClient client, string queue, string body)
        {
            var sender = client.CreateSender(queue);
            await sender.SendMessageAsync(new ServiceBusMessage(body));
        }

        // Assertion-only read helper: callers only assert on the returned message and never settle it.
        // Uses ReceiveAndDelete (NOT PeekLock) so the message is removed on receipt and cannot reappear
        // once its PeekLock lock would expire. The fixture reuses the same per-run queues across the test
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

        // atomic-commit (cross-entity): receive from A and send to B inside ONE TransactionScope, then
        // Complete the scope. Asserts A's message is consumed AND B receives the message — the happy-path
        // cross-entity guarantee that FullAtomicityViaInfrastructure depends on.
        [RequiresRealServiceBusNamespaceFact]
        public async Task CommittedCrossEntityTransactionConsumesSourceAndDeliversToDestination()
        {
            var client = CreateSharedCrossEntityClient();
            await SeedAsync(client, _namespace.QueueA, "seed-commit");

            var receiver = client.CreateReceiver(_namespace.QueueA, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });
            var sender = client.CreateSender(_namespace.QueueB);

            var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            received.Should().NotBeNull("the seed message must be available on queue A");

            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await sender.SendMessageAsync(new ServiceBusMessage("forwarded-commit"));
                await receiver.CompleteMessageAsync(received);
                scope.Complete();
            }

            var delivered = await ReceiveForAssertionAsync(client, _namespace.QueueB);
            delivered.Should().NotBeNull("the forwarded message must be delivered to queue B after the scope commits");
            delivered.Body.ToString().Should().Be("forwarded-commit");

            // Source message was completed inside the committed scope, so A holds nothing more.
            var leftoverOnA = await ReceiveForAssertionAsync(client, _namespace.QueueA);
            leftoverOnA.Should().BeNull("the source message must be consumed when the scope commits");
        }

        // atomic-rollback (cross-entity): receive from A and send to B inside one TransactionScope, then throw
        // BEFORE scope.Complete() → B must NOT receive the message AND A's message must be redelivered (the
        // PeekLock is released because CompleteMessageAsync never committed).
        [RequiresRealServiceBusNamespaceFact]
        public async Task RolledBackCrossEntityTransactionDeliversNothingAndRedeliversSource()
        {
            var client = CreateSharedCrossEntityClient();
            await SeedAsync(client, _namespace.QueueA, "seed-rollback");

            var receiver = client.CreateReceiver(_namespace.QueueA, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });
            var sender = client.CreateSender(_namespace.QueueB);

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

            var deliveredToB = await ReceiveForAssertionAsync(client, _namespace.QueueB);
            deliveredToB.Should().BeNull("the forwarded send must roll back when the scope does not complete");

            var redeliveredOnA = await ReceiveForAssertionAsync(client, _namespace.QueueA);
            redeliveredOnA.Should().NotBeNull("the source message must be redelivered when the settle rolls back");
        }
    }
}
