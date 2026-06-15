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
    /// Characterizes the relay host's fan-out dedup (#222). One processor must exist per distinct change-feed SOURCE
    /// IDENTITY, where the dedup key is DECLARED-OR-GROUND-TRUTH and never INFERRED from an untrusted handle:
    /// <list type="bullet">
    /// <item>
    /// ADVANCED PATH: the caller controls the resolved handle, so the relay keys on the caller-DECLARED
    /// <c>(monitored, lease)</c> identity. Same declared identity ⇒ one processor; distinct ⇒ distinct processors.
    /// </item>
    /// <item>
    /// PLAIN PATH: the handle is provider-derived ground truth, so the key is the COMPLETE resolved identity — account
    /// ENDPOINT + database id + container id, for both monitored and lease. Adding the endpoint closes the cross-account
    /// collapse: identically-named containers in DIFFERENT accounts stay distinct.
    /// </item>
    /// </list>
    /// </summary>
    public class WhenSelectingProcessors
    {
        private const string ProcessorNamePrefix = "chatter-cosmos-outbox-relay";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private sealed class CreateOrder : ICommand { }
        private sealed class ShipOrder : ICommand { }

        // A mocked Cosmos Container whose resolved physical identity (.Id + .Database.Id) and account endpoint
        // (.Database.Client.Endpoint) are fixed, so the host's ground-truth-key dedup can be exercised without a live SDK.
        // Container.Database is abstract, Database.Client is abstract, and CosmosClient.Endpoint is public virtual — all
        // mockable in SDK 3.61.0 (CosmosClient has a protected ctor Moq can use).
        private static Container PhysicalContainer(string databaseId, string containerId, string endpoint = "https://acct.documents.azure.com/")
        {
            var client = new Mock<CosmosClient>();
            client.SetupGet(c => c.Endpoint).Returns(new Uri(endpoint));

            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns(databaseId);
            database.SetupGet(d => d.Client).Returns(client.Object);

            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns(containerId);
            container.SetupGet(c => c.Database).Returns(database.Object);
            return container.Object;
        }

        private static DocumentReliabilityRegistration AdvancedRegistration<TCommand>(string monitoredSourceIdentity,
                                                                                      string leaseSourceIdentity,
                                                                                      Container resolvedDocument,
                                                                                      Container resolvedLease)
            where TCommand : ICommand
        {
            var syntheticIdentity = typeof(TCommand).FullName;
            return new DocumentReliabilityRegistration(
                typeof(TCommand),
                syntheticIdentity,
                syntheticIdentity + ":document",
                syntheticIdentity + ":lease",
                _ => new PartitionKey("pk"),
                PartitionKeyPath,
                documentContainerFactory: _ => resolvedDocument,
                leaseContainerFactory: _ => resolvedLease,
                declaredSourceIdentity: new CosmosSourceIdentity(monitoredSourceIdentity, leaseSourceIdentity));
        }

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

        // (a) PRESERVE iter-1: two command types whose advanced factories resolve the SAME declared source identity must
        // collapse to ONE descriptor with one shared processorName.
        [Fact]
        public void MustCollapseTwoCommandTypesDeclaringTheSameSourceIdentityToOneProcessor()
        {
            // Each command type carries its own synthetic cache triple but DECLARES the same change-feed source identity;
            // the resolved handles are caller-controlled and (on this path) not read for the key. Deduping on the declared
            // identity must yield exactly one processor.
            Container document = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");

            var factory = FactoryWithClient(new Mock<CosmosClient>(MockBehavior.Strict).Object);
            CosmosOutboxRelayHostedService host = Host(
                factory,
                AdvancedRegistration<CreateOrder>("orders-source", "orders-lease-source", document, lease),
                AdvancedRegistration<ShipOrder>("orders-source", "orders-lease-source", document, lease));

            IReadOnlyList<CosmosOutboxRelayHostedService.RelayProcessorDescriptor> descriptors = host.DistinctResolvedProcessorDescriptors();

            descriptors.Should().HaveCount(1, "two command types declaring the same change-feed source identity share one source and must drive exactly one processor");
            descriptors.Single().MonitoredContainer.Should().BeSameAs(document);
            descriptors.Single().LeaseContainer.Should().BeSameAs(lease);
        }

        // (b) iter-2 silent-collapse REGRESSION (plain path): two registrations naming the SAME db/container/lease NAMES
        // but resolving against DIFFERENT accounts (distinct endpoints) must stay TWO descriptors.
        [Fact]
        public void MustKeepTwoProcessorsForSameNamesInDifferentAccountsOnThePlainPath()
        {
            // The four-tuple (monitored.Database.Id / .Id / lease.Database.Id / .Id) is IDENTICAL across both accounts —
            // only the account endpoint differs. Without the endpoint in the key these would collapse and the second
            // source would be silently skipped (never published). The complete ground-truth key must keep them distinct.
            Container accountADocument = PhysicalContainer("shop", "orders", "https://account-a.documents.azure.com/");
            Container accountALease = PhysicalContainer("shop", "orders-leases", "https://account-a.documents.azure.com/");
            Container accountBDocument = PhysicalContainer("shop", "orders", "https://account-b.documents.azure.com/");
            Container accountBLease = PhysicalContainer("shop", "orders-leases", "https://account-b.documents.azure.com/");

            var factory = FactoryWithClient(new Mock<CosmosClient>(MockBehavior.Strict).Object);
            CosmosOutboxRelayHostedService host = Host(
                factory,
                AdvancedRegistration<CreateOrder>("account-a:orders", "account-a:orders-leases", accountADocument, accountALease),
                AdvancedRegistration<ShipOrder>("account-b:orders", "account-b:orders-leases", accountBDocument, accountBLease));

            IReadOnlyList<CosmosOutboxRelayHostedService.RelayProcessorDescriptor> advancedDescriptors = host.DistinctResolvedProcessorDescriptors();

            advancedDescriptors.Should().HaveCount(2, "two distinct declared source identities (one per account) must not collapse");

            // Plain path: same db/container/lease NAMES, distinct account endpoints on the resolved handles → two keys.
            var clientA = new Mock<CosmosClient>();
            clientA.Setup(c => c.GetContainer("shop", "orders")).Returns(accountADocument);
            clientA.Setup(c => c.GetContainer("shop", "orders-leases")).Returns(accountALease);

            // Two separate registries cannot host the same command type twice across one host, so a single host whose two
            // plain registrations resolve through one client to handles carrying DIFFERENT endpoints exercises the key.
            var mixedClient = new Mock<CosmosClient>();
            mixedClient.Setup(c => c.GetContainer("shop", "orders")).Returns(accountADocument);
            mixedClient.Setup(c => c.GetContainer("shop", "orders-leases")).Returns(accountALease);
            mixedClient.Setup(c => c.GetContainer("shopB", "orders")).Returns(accountBDocument);
            mixedClient.Setup(c => c.GetContainer("shopB", "orders-leases")).Returns(accountBLease);

            CosmosOutboxRelayHostedService plainHost = Host(
                FactoryWithClient(mixedClient.Object),
                PlainRegistration<CreateOrder>("shop", "orders", "orders-leases"),
                PlainRegistration<ShipOrder>("shopB", "orders", "orders-leases"));

            IReadOnlyList<CosmosOutboxRelayHostedService.RelayProcessorDescriptor> plainDescriptors = plainHost.DistinctResolvedProcessorDescriptors();
            plainDescriptors.Should().HaveCount(2, "two plain registrations whose resolved handles carry distinct account endpoints must stay distinct processors");
        }

        // (c) PRESERVE: two registrations naming the same db/container/lease over ONE client (plain, same endpoint) must
        // collapse to ONE descriptor.
        [Fact]
        public void MustCollapseThePlainPathResolvingToTheSameGroundTruthIdentityToOneProcessor()
        {
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

            descriptors.Should().HaveCount(1, "two registrations naming the same database/container/lease resolve to one ground-truth source identity and must drive one processor");
        }

        // (d) processorName derived from the COMPLETE key: identical across paths resolving to the same source identity,
        // distinct for distinct sources, with the stable prefix retained.
        [Fact]
        public void MustDeriveProcessorNameFromTheCompleteSourceIdentityKeyWithTheStablePrefix()
        {
            Container document = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");

            var factory = FactoryWithClient(new Mock<CosmosClient>(MockBehavior.Strict).Object);

            CosmosOutboxRelayHostedService advancedHost = Host(
                factory,
                AdvancedRegistration<CreateOrder>("orders-source", "orders-lease-source", document, lease),
                AdvancedRegistration<ShipOrder>("orders-source", "orders-lease-source", document, lease));

            CosmosOutboxRelayHostedService.RelayProcessorDescriptor advancedDescriptor = advancedHost.DistinctResolvedProcessorDescriptors().Single();
            string advancedProcessorName = advancedDescriptor.ProcessorName;

            advancedProcessorName.Should().StartWith(ProcessorNamePrefix + ":", "the stable processor-name prefix must be retained");
            advancedProcessorName.Should().NotContain("synthetic", "the processor name must be derived from the complete source-identity key, not the synthetic cache triple");

            // Two advanced registrations declaring the same identity share the identical processor name (the dedup leaves
            // one descriptor).
            advancedHost.DistinctResolvedProcessorDescriptors().Single().ProcessorName.Should().Be(advancedProcessorName);

            // A distinct declared source identity must yield a DISTINCT processor name.
            CosmosOutboxRelayHostedService otherSourceHost = Host(
                FactoryWithClient(new Mock<CosmosClient>(MockBehavior.Strict).Object),
                AdvancedRegistration<CreateOrder>("ledger-source", "ledger-lease-source", document, lease));
            otherSourceHost.DistinctResolvedProcessorDescriptors().Single().ProcessorName
                .Should().NotBe(advancedProcessorName, "two distinct declared source identities must derive distinct processor names");

            // The plain path over the same physical ground-truth identity derives a STABLE, prefix-retaining name too. It
            // need not equal the advanced declared-identity name (the two key paths are sourced differently), but it must
            // carry the stable prefix and not leak the synthetic cache triple.
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer("shop", "orders")).Returns(document);
            client.Setup(c => c.GetContainer("shop", "orders-leases")).Returns(lease);
            CosmosOutboxRelayHostedService plainHost = Host(
                FactoryWithClient(client.Object),
                PlainRegistration<CreateOrder>("shop", "orders", "orders-leases"));

            string plainProcessorName = plainHost.DistinctResolvedProcessorDescriptors().Single().ProcessorName;
            plainProcessorName.Should().StartWith(ProcessorNamePrefix + ":", "the plain-path processor name must retain the stable prefix");
            plainProcessorName.Should().NotContain("synthetic", "the plain-path processor name is derived from the ground-truth key, never the synthetic cache triple");

            // A second plain host over the SAME physical ground-truth identity derives the IDENTICAL name (stable per
            // source so cooperating hosts agree on one logical processor).
            var client2 = new Mock<CosmosClient>();
            client2.Setup(c => c.GetContainer("shop", "orders")).Returns(document);
            client2.Setup(c => c.GetContainer("shop", "orders-leases")).Returns(lease);
            CosmosOutboxRelayHostedService plainHost2 = Host(
                FactoryWithClient(client2.Object),
                PlainRegistration<ShipOrder>("shop", "orders", "orders-leases"));
            plainHost2.DistinctResolvedProcessorDescriptors().Single().ProcessorName
                .Should().Be(plainProcessorName, "the same ground-truth source identity must yield the identical processor name across hosts");
        }

        // (e) INJECTIVITY REGRESSION (#222, 3rd iteration): two ADVANCED registrations whose declared (monitored, lease)
        // component pairs are DISTINCT but flatten to the SAME flat \0-joined string — (monitored="a\0b", lease="c") and
        // (monitored="a", lease="b\0c") both flattened to "declared\0a\0b\0c" under the OLD key — must now yield TWO
        // descriptors with TWO DISTINCT processor names. The typed component-wise key compares Monitored and Lease
        // separately, so a delimiter byte inside one component cannot bleed across the component boundary and collapse the
        // second source (silent non-publish). This is the closed-by-construction structural fix: no representable byte in a
        // component can spoof a boundary because no boundary byte exists in the equality decision.
        [Fact]
        public void MustNotCollapseDeclaredIdentitiesThatFlattenToTheSameDelimiterJoinedString()
        {
            Container document = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");

            var factory = FactoryWithClient(new Mock<CosmosClient>(MockBehavior.Strict).Object);
            CosmosOutboxRelayHostedService host = Host(
                factory,
                AdvancedRegistration<CreateOrder>("a\0b", "c", document, lease),
                AdvancedRegistration<ShipOrder>("a", "b\0c", document, lease));

            IReadOnlyList<CosmosOutboxRelayHostedService.RelayProcessorDescriptor> descriptors = host.DistinctResolvedProcessorDescriptors();

            descriptors.Should().HaveCount(2, "distinct declared component pairs that only collide when flattened to a delimiter-joined string must stay distinct under the typed component-wise key");
            descriptors.Select(descriptor => descriptor.ProcessorName).Distinct().Should().HaveCount(2,
                "distinct source identities must derive distinct processor names from the injective length-prefixed encoding");
        }
    }
}
