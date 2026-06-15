using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes the relay host's fan-out dedup (#222 high finding 3fa6353b). One processor must exist per distinct
    /// PHYSICAL (monitored, lease) container pair, keyed on the RESOLVED container identity
    /// (<c>monitored.Database.Id</c>/<c>monitored.Id</c>/<c>lease.Database.Id</c>/<c>lease.Id</c>) — NOT on the
    /// registration's <c>(Database, ContainerName, LeaseName)</c> triple. The advanced overload synthesizes that triple
    /// from the command type while resolving the SAME physical containers via app factories, so deduping on the triple
    /// would split one physical lease into two processors and double-publish every pending outbox doc (ADR-0008).
    /// </summary>
    public class WhenSelectingProcessors
    {
        private const string ProcessorNamePrefix = "chatter-cosmos-outbox-relay";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private sealed class CreateOrder : ICommand { }
        private sealed class ShipOrder : ICommand { }

        // A mocked Cosmos Container whose resolved physical identity (.Id + .Database.Id) is fixed, so the host's
        // physical-key dedup can be exercised without a live SDK.
        private static Container PhysicalContainer(string databaseId, string containerId)
        {
            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns(databaseId);

            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns(containerId);
            container.SetupGet(c => c.Database).Returns(database.Object);
            return container.Object;
        }

        private static DocumentReliabilityRegistration AdvancedRegistration<TCommand>(string syntheticDatabase,
                                                                                      string syntheticContainer,
                                                                                      string syntheticLease,
                                                                                      Container resolvedDocument,
                                                                                      Container resolvedLease)
            where TCommand : ICommand
            => new DocumentReliabilityRegistration(
                typeof(TCommand),
                syntheticDatabase,
                syntheticContainer,
                syntheticLease,
                _ => new PartitionKey("pk"),
                PartitionKeyPath,
                documentContainerFactory: _ => resolvedDocument,
                leaseContainerFactory: _ => resolvedLease);

        private static DocumentReliabilityRegistration PlainRegistration<TCommand>(string database, string container, string lease)
            where TCommand : ICommand
            => new DocumentReliabilityRegistration(
                typeof(TCommand),
                database,
                container,
                lease,
                _ => new PartitionKey("pk"),
                PartitionKeyPath);

        private static CosmosOutboxRelayHostedService Host(CosmosContainerFactory containerFactory, params DocumentReliabilityRegistration[] registrations)
        {
            var registry = new DocumentReliabilityRegistry();
            foreach (DocumentReliabilityRegistration registration in registrations)
            {
                // Add is internal; InternalsVisibleTo exposes it to the test assembly.
                registry.Add(registration);
            }

            return new CosmosOutboxRelayHostedService(
                registry,
                containerFactory,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>());
        }

        private static CosmosContainerFactory FactoryWithClient(CosmosClient client)
        {
            var services = new ServiceCollection();
            if (client is not null)
            {
                services.AddSingleton(client);
            }
            return new CosmosContainerFactory(services.BuildServiceProvider());
        }

        [Fact]
        public void MustCollapseTwoCommandTypesResolvingTheSamePhysicalContainersToOneProcessor()
        {
            // Advanced overload: each command type carries a DISTINCT synthetic triple but its factories resolve the SAME
            // physical document+lease containers. Deduping on the resolved identity must yield exactly one processor.
            Container document = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");

            var factory = FactoryWithClient(new Mock<CosmosClient>(MockBehavior.Strict).Object);
            CosmosOutboxRelayHostedService host = Host(
                factory,
                AdvancedRegistration<CreateOrder>("synthetic:CreateOrder", "synthetic:CreateOrder:doc", "synthetic:CreateOrder:lease", document, lease),
                AdvancedRegistration<ShipOrder>("synthetic:ShipOrder", "synthetic:ShipOrder:doc", "synthetic:ShipOrder:lease", document, lease));

            IReadOnlyList<CosmosOutboxRelayHostedService.RelayProcessorDescriptor> descriptors = host.DistinctResolvedProcessorDescriptors();

            descriptors.Should().HaveCount(1, "two command types resolving the same physical document+lease containers share one physical lease and must drive exactly one processor");
            descriptors.Single().MonitoredContainer.Should().BeSameAs(document);
            descriptors.Single().LeaseContainer.Should().BeSameAs(lease);
        }

        [Fact]
        public void MustKeepTwoProcessorsForCommandTypesResolvingDifferentPhysicalContainers()
        {
            // Guard against over-collapsing: distinct physical containers must remain distinct processors.
            Container ordersDoc = PhysicalContainer("shop", "orders");
            Container ordersLease = PhysicalContainer("shop", "orders-leases");
            Container ledgerDoc = PhysicalContainer("fin", "ledger");
            Container ledgerLease = PhysicalContainer("fin", "ledger-leases");

            var factory = FactoryWithClient(new Mock<CosmosClient>(MockBehavior.Strict).Object);
            CosmosOutboxRelayHostedService host = Host(
                factory,
                AdvancedRegistration<CreateOrder>("synthetic:CreateOrder", "synthetic:CreateOrder:doc", "synthetic:CreateOrder:lease", ordersDoc, ordersLease),
                AdvancedRegistration<ShipOrder>("synthetic:ShipOrder", "synthetic:ShipOrder:doc", "synthetic:ShipOrder:lease", ledgerDoc, ledgerLease));

            IReadOnlyList<CosmosOutboxRelayHostedService.RelayProcessorDescriptor> descriptors = host.DistinctResolvedProcessorDescriptors();

            descriptors.Should().HaveCount(2, "distinct physical container pairs must not be collapsed into one processor");
            descriptors.Select(d => d.MonitoredContainer).Should().Contain(new[] { ordersDoc, ledgerDoc });
        }

        [Fact]
        public void MustCollapseThePlainTriplePathResolvingViaGetContainerToOneProcessor()
        {
            // Plain path: two registrations naming the SAME database/container/lease resolve (via the mocked
            // CosmosClient.GetContainer + the factory's per-(database,container) cache) to the same physical handles, so
            // the new physical-key dedup must still yield exactly one processor.
            Container document = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");

            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer("shop", "orders")).Returns(document);
            client.Setup(c => c.GetContainer("shop", "orders-leases")).Returns(lease);

            var factory = FactoryWithClient(client.Object);
            CosmosOutboxRelayHostedService host = Host(
                factory,
                PlainRegistration<CreateOrder>("shop", "orders", "orders-leases"),
                PlainRegistration<ShipOrder>("shop", "orders", "orders-leases"));

            IReadOnlyList<CosmosOutboxRelayHostedService.RelayProcessorDescriptor> descriptors = host.DistinctResolvedProcessorDescriptors();

            descriptors.Should().HaveCount(1, "two registrations naming the same database/container/lease resolve to one physical container pair and must drive one processor");
        }

        [Fact]
        public void MustDeriveProcessorNameFromThePhysicalKeyWithTheStablePrefix()
        {
            // processorName must be derived from the RESOLVED physical key (not the synthetic triple) so all app instances
            // sharing a physical lease cooperate on one logical processor; the stable prefix must be retained.
            Container document = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");

            var factory = FactoryWithClient(new Mock<CosmosClient>(MockBehavior.Strict).Object);

            CosmosOutboxRelayHostedService advancedHost = Host(
                factory,
                AdvancedRegistration<CreateOrder>("synthetic:CreateOrder", "synthetic:CreateOrder:doc", "synthetic:CreateOrder:lease", document, lease),
                AdvancedRegistration<ShipOrder>("synthetic:ShipOrder", "synthetic:ShipOrder:doc", "synthetic:ShipOrder:lease", document, lease));

            string processorName = advancedHost.DistinctResolvedProcessorDescriptors().Single().ProcessorName;

            processorName.Should().StartWith(ProcessorNamePrefix + ":", "the stable processor-name prefix must be retained");
            processorName.Should().NotContain("synthetic", "the processor name must be derived from the resolved physical key, not the synthetic triple");

            // Two same-physical command types share the identical processor name (the dedup leaves one descriptor, and a
            // separate plain-path host over the same physical containers derives the SAME name).
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer("shop", "orders")).Returns(document);
            client.Setup(c => c.GetContainer("shop", "orders-leases")).Returns(lease);
            CosmosOutboxRelayHostedService plainHost = Host(
                FactoryWithClient(client.Object),
                PlainRegistration<CreateOrder>("shop", "orders", "orders-leases"));

            string plainProcessorName = plainHost.DistinctResolvedProcessorDescriptors().Single().ProcessorName;
            plainProcessorName.Should().Be(processorName, "the same resolved physical containers must yield the identical physical-key-derived processor name regardless of registration path");
        }
    }
}
