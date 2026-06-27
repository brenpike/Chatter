using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingStandaloneCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes the STANDALONE relay host's processor selection (#222 standalone DI surface). Unlike the
    /// registry-driven <see cref="CosmosOutboxRelayHostedService"/> — which derives one processor per distinct change-feed
    /// source identity in the <see cref="DocumentReliabilityRegistry"/> — the standalone host derives its SINGLE processor
    /// descriptor from the <see cref="CosmosOutboxRelayOptions"/> (monitored + lease container factories, partition-key
    /// path). Its processor name is built from the SAME injective source-identity derivation the registry host uses, so a
    /// standalone relay coexists with the command-pipeline relay without a processor-name collision.
    /// </summary>
    public class WhenSelectingProcessors
    {
        private const string ProcessorNamePrefix = "chatter-cosmos-outbox-relay";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private sealed class CreateOrder : ICommand { }

        // A scoped dependency the body-resolver factory resolves from each per-document scope; its disposal is the
        // observable signal that the document's DI scope was disposed after the document was drained.
        private sealed class ScopedTracker : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }

        private static Stream StreamOf(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

        // Mirrors the registry-host characterization test's mocked Container: resolved physical identity (.Id +
        // .Database.Id) and account endpoint (.Database.Client.Endpoint) are fixed so the ground-truth source-identity key
        // can be exercised without a live SDK.
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

        private static CosmosOutboxRelayOptions OptionsFor(Container monitored,
                                                           Container lease,
                                                           string monitoredSourceIdentity = null,
                                                           string leaseSourceIdentity = null)
            => new CosmosOutboxRelayOptions
            {
                MonitoredContainerFactory = _ => monitored,
                LeaseContainerFactory = _ => lease,
                PartitionKeyPath = PartitionKeyPath,
                MonitoredSourceIdentity = monitoredSourceIdentity,
                LeaseSourceIdentity = leaseSourceIdentity,
            };

        private static StandaloneCosmosOutboxRelayHostedService StandaloneHost(CosmosOutboxRelayOptions options)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options);

        // A registry-driven host over a single plain registration whose ground-truth resolves to the supplied handles, so a
        // test can compare its processor names against a standalone host's name.
        private static CosmosOutboxRelayHostedService RegistryHostOverPlainSource(string database,
                                                                                 string container,
                                                                                 string lease,
                                                                                 Container monitored,
                                                                                 Container leaseContainer)
        {
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer(database, container)).Returns(monitored);
            client.Setup(c => c.GetContainer(database, lease)).Returns(leaseContainer);

            var services = new ServiceCollection();
            services.AddSingleton(client.Object);
            var factory = new CosmosContainerFactory(services.BuildServiceProvider());

            var registry = new DocumentReliabilityRegistry();
            registry.Add(new DocumentReliabilityRegistration(
                typeof(CreateOrder),
                database,
                container,
                lease,
                _ => new PartitionKey("pk"),
                PartitionKeyPath));

            return new CosmosOutboxRelayHostedService(
                registry,
                factory,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>());
        }

        // 8. The standalone host derives its single processor descriptor from the configured monitored + lease containers
        // and partition-key path (resolved from options, not the registry).
        [Fact]
        public void MustResolveItsProcessorDescriptorFromTheConfiguredContainers()
        {
            Container monitored = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");

            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(OptionsFor(monitored, lease));

            CosmosOutboxRelayHostedService.RelayProcessorDescriptor descriptor = host.ResolveProcessorDescriptor();

            descriptor.MonitoredContainer.Should().BeSameAs(monitored, "the standalone host monitors the container its options factory resolves");
            descriptor.LeaseContainer.Should().BeSameAs(lease, "the standalone host leases against the container its options factory resolves");
            descriptor.PartitionKeyPath.Should().BeSameAs(PartitionKeyPath, "the descriptor carries the configured partition-key path");
            descriptor.ProcessorName.Should().StartWith(ProcessorNamePrefix + ":", "the standalone processor name retains the stable prefix");
        }

        // 9. A standalone relay over a DISTINCT change-feed source coexists with the command-pipeline (registry) relay
        // without a processor-name collision — the names are disjoint because each is derived from its own source identity.
        [Fact]
        public void MustNotCollideWithTheCommandPipelineRelayProcessorName()
        {
            Container registryMonitored = PhysicalContainer("shop", "orders", "https://account-a.documents.azure.com/");
            Container registryLease = PhysicalContainer("shop", "orders-leases", "https://account-a.documents.azure.com/");
            CosmosOutboxRelayHostedService registryHost = RegistryHostOverPlainSource("shop", "orders", "orders-leases", registryMonitored, registryLease);

            IEnumerable<string> registryProcessorNames = registryHost
                .DistinctResolvedProcessorDescriptors()
                .Select(descriptor => descriptor.ProcessorName);

            Container standaloneMonitored = PhysicalContainer("events", "events", "https://account-b.documents.azure.com/");
            Container standaloneLease = PhysicalContainer("events", "events-leases", "https://account-b.documents.azure.com/");
            string standaloneProcessorName = StandaloneHost(OptionsFor(standaloneMonitored, standaloneLease))
                .ResolveProcessorDescriptor()
                .ProcessorName;

            registryProcessorNames.Should().NotContain(standaloneProcessorName,
                "a standalone relay over a distinct change-feed source must not share a processor name with the command-pipeline relay");
        }

        // 10. With an EMPTY registry (no command-pipeline participants) and ONLY the standalone relay configured, exactly
        // one processor exists: the registry host yields zero descriptors and the standalone host yields its single one.
        [Fact]
        public void MustYieldExactlyOneProcessorWhenItIsTheOnlyRelay()
        {
            var emptyRegistry = new DocumentReliabilityRegistry();
            var registryHost = new CosmosOutboxRelayHostedService(
                emptyRegistry,
                new CosmosContainerFactory(new ServiceCollection().BuildServiceProvider()),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>());

            registryHost.DistinctResolvedProcessorDescriptors().Should().BeEmpty("an empty registry contributes no command-pipeline processors");

            Container monitored = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");
            CosmosOutboxRelayHostedService.RelayProcessorDescriptor descriptor = StandaloneHost(OptionsFor(monitored, lease)).ResolveProcessorDescriptor();

            descriptor.MonitoredContainer.Should().BeSameAs(monitored,
                "with no command-pipeline participants the standalone relay is the sole processor over its configured source");
        }

        // Source-identity derivation: when the caller DECLARES a source identity, the standalone key is the declared pair
        // (the ground-truth handle is not read), so two standalone hosts declaring the same identity over different handles
        // derive the IDENTICAL processor name and two distinct declarations derive DISTINCT names.
        [Fact]
        public void MustDeriveProcessorNameFromTheDeclaredSourceIdentityWhenSupplied()
        {
            Container monitoredA = PhysicalContainer("shop", "orders", "https://account-a.documents.azure.com/");
            Container leaseA = PhysicalContainer("shop", "orders-leases", "https://account-a.documents.azure.com/");
            Container monitoredB = PhysicalContainer("warehouse", "orders", "https://account-b.documents.azure.com/");
            Container leaseB = PhysicalContainer("warehouse", "orders-leases", "https://account-b.documents.azure.com/");

            string firstName = StandaloneHost(OptionsFor(monitoredA, leaseA, "orders-source", "orders-lease-source"))
                .ResolveProcessorDescriptor().ProcessorName;
            string sameIdentityDifferentHandles = StandaloneHost(OptionsFor(monitoredB, leaseB, "orders-source", "orders-lease-source"))
                .ResolveProcessorDescriptor().ProcessorName;
            string distinctIdentity = StandaloneHost(OptionsFor(monitoredA, leaseA, "ledger-source", "ledger-lease-source"))
                .ResolveProcessorDescriptor().ProcessorName;

            sameIdentityDifferentHandles.Should().Be(firstName,
                "the declared identity is the key, so the same declared pair derives the identical processor name regardless of the resolved handles");
            distinctIdentity.Should().NotBe(firstName, "a distinct declared source identity derives a distinct processor name");
        }

        // R-STEP-002: a configured body-resolver factory is consulted PER DRAINED DOCUMENT from a FRESH DI scope (not once
        // at host construction, and not from the root provider), and that scope is disposed after the document — so a
        // scoped resolver (and the scoped dependencies it resolves) never outlives the document it drained.
        [Fact]
        public async Task MustOpenResolveAndDisposeAFreshScopePerDrainedDocument()
        {
            var capturedProviders = new List<IServiceProvider>();
            var trackers = new List<ScopedTracker>();

            var rootServices = new ServiceCollection();
            rootServices.AddScoped<ScopedTracker>();
            using ServiceProvider root = rootServices.BuildServiceProvider();

            Container monitored = PhysicalContainer("shop", "orders");
            Container lease = PhysicalContainer("shop", "orders-leases");
            CosmosOutboxRelayOptions options = OptionsFor(monitored, lease);
            options.BodyResolverFactory = scopedProvider =>
            {
                capturedProviders.Add(scopedProvider);
                trackers.Add(scopedProvider.GetRequiredService<ScopedTracker>());
                return Mock.Of<IOutboxBodyResolver>();
            };

            var host = new StandaloneCosmosOutboxRelayHostedService(
                root,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options);

            // Two non-pending documents: each still drives the per-document scope-open + factory-resolve before the relay
            // filters it out as non-admitted, so the scope lifecycle is exercised without needing the publish/stamp path.
            using Stream batch = StreamOf("{\"Documents\":[{\"id\":\"a\"},{\"id\":\"b\"}]}");
            await host.HandleChangesAsync(batch, monitored, PartitionKeyPath, CancellationToken.None);

            capturedProviders.Should().HaveCount(2, "the body-resolver factory is consulted exactly once per drained document");
            capturedProviders[0].Should().NotBeSameAs(capturedProviders[1], "each document is drained under its OWN per-document DI scope");
            capturedProviders.Should().NotContain(root, "the resolver is resolved from a per-document scope, never the root provider");
            trackers.Should().HaveCount(2);
            trackers.Should().OnlyContain(tracker => tracker.Disposed,
                "each per-document scope is disposed after its document, disposing the scoped dependencies the resolver factory resolved");
        }
    }
}
