using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        private const string InfrastructureType = "test-infra";
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

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static JsonElement JsonValue(string raw) => Parse(JsonSerializer.Serialize(raw));

        // Builds a genuinely-admitted pending outbox document wire-faithfully (id == CosmosItemId.ForOutbox(MessageId),
        // status == pending, _chatterType == outbox), mirroring WhenResolvingOutboxBody's builder, so the relay's
        // IsPendingOutbox id-guard admits it and it drives the full publish/stamp path.
        private static JsonElement PendingOutboxDocument(string messageId, string destination, object body, string tenantId)
        {
            var converter = new JsonBodyConverter();
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            var outbound = new OutboundBrokeredMessage(messageId, body, messageContext, destination, converter);
            CosmosOutboxDocument document = CosmosOutboxDocument.From(outbound);

            var partitionKeyValues = new List<JsonElement> { JsonValue(tenantId) };
            JsonObject rendered = document.ToJsonObject(PartitionKeyPath, partitionKeyValues);
            return Parse(rendered.ToJsonString());
        }

        // A monitored container that satisfies the relay's delivered/TTL PatchItemAsync<JsonElement> for an admitted
        // document and returns a benign response, mirroring WhenResolvingOutboxBody's RecordingContainer.
        private static Mock<Container> RecordingMonitoredContainer()
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            return container;
        }

        // A resolver whose ResolveAsync returns a completed null-result Task: the admitted document is stamped delivered
        // and dispatches nothing, so the publish path's infrastructure dispatcher is never touched while the Mock infra
        // stays usable (a bare Mock.Of resolver would return a null Task and fault the await).
        private static IOutboxBodyResolver NullResolvingResolver()
        {
            var resolver = new Mock<IOutboxBodyResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((OutboundBrokeredMessage)null);
            return resolver.Object;
        }

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

        private static StandaloneCosmosOutboxRelayHostedService StandaloneHost(CosmosOutboxRelayOptions options,
                                                                              StandaloneRelayProcessorRegistry processorRegistry)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options,
                processorRegistry);

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

        // PR2-STEP-001 start-time backstop: two GROUND-TRUTH-defaulted standalone hosts over the SAME physical identity
        // (same monitored endpoint/db/container + same lease endpoint/db/container) resolve to the same ground-truth processor
        // name. Sharing one registry, the first start-time registration succeeds and the second throws InvalidOperationException
        // naming the colliding processor name — the consumer-group wedge is rejected at host start rather than silently formed.
        [Fact]
        public void MustThrowAtStartTimeWhenTwoGroundTruthDefaultedHostsResolveToTheSamePhysicalIdentity()
        {
            var registry = new StandaloneRelayProcessorRegistry();

            StandaloneCosmosOutboxRelayHostedService first = StandaloneHost(
                OptionsFor(PhysicalContainer("shop", "orders"), PhysicalContainer("shop", "orders-leases")), registry);
            StandaloneCosmosOutboxRelayHostedService second = StandaloneHost(
                OptionsFor(PhysicalContainer("shop", "orders"), PhysicalContainer("shop", "orders-leases")), registry);

            first.RegisterStartTimeProcessorIdentity(first.ResolveProcessorDescriptor());

            CosmosOutboxRelayHostedService.RelayProcessorDescriptor secondDescriptor = second.ResolveProcessorDescriptor();
            Action act = () => second.RegisterStartTimeProcessorIdentity(secondDescriptor);

            act.Should().Throw<InvalidOperationException>(
                "two ground-truth-defaulted hosts over the same physical identity resolve to one processor name + lease — a consumer-group wedge rejected at host start")
                .WithMessage("*" + secondDescriptor.ProcessorName + "*", "the ground-truth collision message names the colliding processor name");
        }

        [Fact]
        public void MustRegisterBothWhenTwoGroundTruthDefaultedHostsResolveToDistinctPhysicalIdentities()
        {
            var registry = new StandaloneRelayProcessorRegistry();

            StandaloneCosmosOutboxRelayHostedService first = StandaloneHost(
                OptionsFor(PhysicalContainer("shop", "orders"), PhysicalContainer("shop", "orders-leases")), registry);
            StandaloneCosmosOutboxRelayHostedService second = StandaloneHost(
                OptionsFor(PhysicalContainer("events", "events"), PhysicalContainer("events", "events-leases")), registry);

            first.RegisterStartTimeProcessorIdentity(first.ResolveProcessorDescriptor());
            Action act = () => second.RegisterStartTimeProcessorIdentity(second.ResolveProcessorDescriptor());

            act.Should().NotThrow("distinct physical identities resolve to distinct ground-truth processor names, so both register");
        }

        // A DECLARED host is guarded at REGISTRATION; the start-time backstop SKIPS it so it never self-collides on the name
        // the registration guard already accumulated.
        [Fact]
        public void MustSkipStartTimeRegistrationForADeclaredHostAlreadyInTheRegistry()
        {
            var registry = new StandaloneRelayProcessorRegistry();

            StandaloneCosmosOutboxRelayHostedService declaredHost = StandaloneHost(
                OptionsFor(PhysicalContainer("shop", "orders"), PhysicalContainer("shop", "orders-leases"), "orders-source", "orders-lease-source"),
                registry);
            CosmosOutboxRelayHostedService.RelayProcessorDescriptor descriptor = declaredHost.ResolveProcessorDescriptor();

            // Simulate the registration-time guard having already accumulated this declared host's processor name.
            registry.RegisterDeclaredProcessorOrThrow(descriptor.ProcessorName, "orders-source", "orders-lease-source");

            Action act = () => declaredHost.RegisterStartTimeProcessorIdentity(descriptor);

            act.Should().NotThrow(
                "a declared host is registration-guarded, so the start-time backstop skips it rather than self-colliding on its own already-registered name");
        }

        // A host built via the legacy 4-arg ctor (null registry) performs no start-time registration — the backstop is a no-op.
        [Fact]
        public void MustBeANoOpStartTimeWhenConstructedWithoutARegistry()
        {
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(
                OptionsFor(PhysicalContainer("shop", "orders"), PhysicalContainer("shop", "orders-leases")));

            Action act = () => host.RegisterStartTimeProcessorIdentity(host.ResolveProcessorDescriptor());

            act.Should().NotThrow("a host with no registry performs no start-time processor-identity registration");
        }

        // R3-STEP-001: the host opens a DI scope + invokes the body-resolver factory ONLY for an ADMITTED (pending outbox)
        // document — NOT per raw change-feed document. Over a mixed batch (one genuinely-admitted pending outbox doc plus
        // two non-admitted docs that co-reside in the monitored container), the factory is consulted exactly once (the
        // admitted doc), no scope is opened for the non-admitted docs, and the one admitted scope is disposed after its
        // document. This is the liveness fix: the admission check precedes any scope/factory/user-DI work.
        [Fact]
        public async Task MustOpenResolveAndDisposeAScopeOnlyForTheAdmittedDocument()
        {
            var capturedProviders = new List<IServiceProvider>();
            var trackers = new List<ScopedTracker>();

            var rootServices = new ServiceCollection();
            rootServices.AddScoped<ScopedTracker>();
            using ServiceProvider root = rootServices.BuildServiceProvider();

            Mock<Container> monitored = RecordingMonitoredContainer();
            Container lease = PhysicalContainer("shop", "orders-leases");
            CosmosOutboxRelayOptions options = OptionsFor(monitored.Object, lease);
            options.BodyResolverFactory = scopedProvider =>
            {
                capturedProviders.Add(scopedProvider);
                trackers.Add(scopedProvider.GetRequiredService<ScopedTracker>());
                return NullResolvingResolver();
            };

            var host = new StandaloneCosmosOutboxRelayHostedService(
                root,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options);

            // A mixed batch: ONE genuinely-admitted pending outbox doc PLUS two non-admitted co-resident docs (a bare item
            // and an inbox/domain-shaped item). Only the admitted doc may open a scope + invoke the factory.
            JsonElement admitted = PendingOutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            string batchJson = $"{{\"Documents\":[{{\"id\":\"a\"}},{admitted.GetRawText()},{{\"id\":\"inbox:x\",\"_chatterType\":\"inbox\",\"MessageId\":\"m\"}}]}}";
            using Stream batch = StreamOf(batchJson);
            await host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, CancellationToken.None);

            capturedProviders.Should().ContainSingle("the body-resolver factory is consulted exactly once — only for the admitted pending outbox document, not per raw drained document");
            capturedProviders.Should().NotContain(root, "the resolver is resolved from a per-document scope, never the root provider");
            trackers.Should().ContainSingle();
            trackers[0].Disposed.Should().BeTrue("the admitted document's scope is disposed after it is drained");
            monitored.Verify(c => c.PatchItemAsync<JsonElement>(
                    It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                    It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "only the admitted document is stamped delivered+ttl; the non-admitted docs are skipped");
        }

        // R3-STEP-001 liveness A: a body-resolver factory that THROWS, over a batch of ONLY non-admitted docs (a domain
        // write, an inbox marker, a malformed item), must NOT be invoked at all and HandleChangesAsync must complete
        // WITHOUT throwing — a co-resident domain/inbox/malformed write can never run user DI nor wedge the change feed.
        [Fact]
        public async Task MustNotInvokeAThrowingFactoryNorWedgeTheFeedForNonAdmittedDocuments()
        {
            var factoryInvocations = 0;

            using ServiceProvider root = new ServiceCollection().BuildServiceProvider();

            Mock<Container> monitored = RecordingMonitoredContainer();
            Container lease = PhysicalContainer("shop", "orders-leases");
            CosmosOutboxRelayOptions options = OptionsFor(monitored.Object, lease);
            options.BodyResolverFactory = _ =>
            {
                factoryInvocations++;
                throw new InvalidOperationException("resolver factory must never run for a non-admitted document");
            };

            var host = new StandaloneCosmosOutboxRelayHostedService(
                root,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options);

            // Only non-admitted, co-resident shapes: a bare domain doc, an inbox marker, a malformed outbox-discriminated
            // doc with no status. None is a pending outbox document, so none may open a scope or invoke the factory.
            using Stream batch = StreamOf("{\"Documents\":[{\"id\":\"a\"},{\"id\":\"inbox:x\",\"_chatterType\":\"inbox\",\"MessageId\":\"m\"},{\"id\":\"outbox:y\",\"_chatterType\":\"outbox\"}]}");

            Func<Task> act = () => host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, CancellationToken.None);

            await act.Should().NotThrowAsync("a batch of only non-admitted documents must not run user DI nor wedge the change feed");
            factoryInvocations.Should().Be(0, "the body-resolver factory is never invoked for a non-admitted document");
        }

        // R3-STEP-001 liveness B: for an ADMITTED document whose resolver throws, HandleChangesAsync PROPAGATES — the SDK
        // does not checkpoint and the document re-surfaces (at-least-once), so a genuine resolver failure is NOT silently
        // swallowed by the new admission pre-check.
        [Fact]
        public async Task MustPropagateWhenAnAdmittedDocumentsResolverThrows()
        {
            var resolveFailure = new InvalidOperationException("resolver unavailable");

            using ServiceProvider root = new ServiceCollection().BuildServiceProvider();

            Mock<Container> monitored = RecordingMonitoredContainer();
            Container lease = PhysicalContainer("shop", "orders-leases");
            CosmosOutboxRelayOptions options = OptionsFor(monitored.Object, lease);
            options.BodyResolverFactory = _ =>
            {
                var resolver = new Mock<IOutboxBodyResolver>();
                resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                        .ThrowsAsync(resolveFailure);
                return resolver.Object;
            };

            var host = new StandaloneCosmosOutboxRelayHostedService(
                root,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options);

            JsonElement admitted = PendingOutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            using Stream batch = StreamOf($"{{\"Documents\":[{admitted.GetRawText()}]}}");

            Func<Task> act = () => host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(resolveFailure,
                "an admitted document's resolver failure propagates so the SDK does not checkpoint and the document re-surfaces");
            monitored.Verify(c => c.PatchItemAsync<JsonElement>(
                    It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                    It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "a resolver failure leaves the document pending — no delivered/ttl stamp");
        }

        // R4-STEP-001: the caller-supplied AdditionalPendingFilter is composed at EXACTLY ONE admission site (inside the
        // relay), never twice. Over a mixed batch with exactly one admitted pending outbox doc, a counting filter is
        // invoked exactly once and the doc is stamped delivered exactly once. Before the single-by-construction fix the
        // host pre-scope gate re-evaluated the full admission — including this caller delegate — so the delegate ran twice
        // per admitted document.
        [Fact]
        public async Task MustEvaluateTheAdditionalPendingFilterExactlyOncePerAdmittedDocument()
        {
            var filterInvocations = 0;

            using ServiceProvider root = new ServiceCollection().BuildServiceProvider();

            Mock<Container> monitored = RecordingMonitoredContainer();
            Container lease = PhysicalContainer("shop", "orders-leases");
            CosmosOutboxRelayOptions options = OptionsFor(monitored.Object, lease);
            options.AdditionalPendingFilter = _ =>
            {
                filterInvocations++;
                return true;
            };
            options.BodyResolverFactory = _ => NullResolvingResolver();

            var host = new StandaloneCosmosOutboxRelayHostedService(
                root,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options);

            JsonElement admitted = PendingOutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            string batchJson = $"{{\"Documents\":[{{\"id\":\"a\"}},{admitted.GetRawText()},{{\"id\":\"inbox:x\",\"_chatterType\":\"inbox\",\"MessageId\":\"m\"}}]}}";
            using Stream batch = StreamOf(batchJson);

            await host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, CancellationToken.None);

            filterInvocations.Should().Be(1,
                "the caller-supplied AdditionalPendingFilter is composed at exactly one admission site (the relay), so an admitted document evaluates it once — not twice");
            monitored.Verify(c => c.PatchItemAsync<JsonElement>(
                    It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                    It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "the admitted document is stamped delivered exactly once");
        }

        // R4-STEP-001 no-wedge A: a NON-IDEMPOTENT AdditionalPendingFilter (true on the first evaluation, false on the
        // second) must NOT leave a genuine pending outbox document undrained. Under single-by-construction admission the
        // relay evaluates the filter exactly once, so the document drains and stamps delivered. Before the fix the host
        // gate's first evaluation returned true and the relay's second evaluation returned false, wedging the document.
        [Fact]
        public async Task MustNotWedgeAGenuinePendingDocumentUnderANonIdempotentFilter()
        {
            var filterInvocations = 0;

            using ServiceProvider root = new ServiceCollection().BuildServiceProvider();

            Mock<Container> monitored = RecordingMonitoredContainer();
            Container lease = PhysicalContainer("shop", "orders-leases");
            CosmosOutboxRelayOptions options = OptionsFor(monitored.Object, lease);
            options.AdditionalPendingFilter = _ => Interlocked.Increment(ref filterInvocations) == 1;
            options.BodyResolverFactory = _ => NullResolvingResolver();

            var host = new StandaloneCosmosOutboxRelayHostedService(
                root,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options);

            JsonElement admitted = PendingOutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            using Stream batch = StreamOf($"{{\"Documents\":[{admitted.GetRawText()}]}}");

            await host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, CancellationToken.None);

            filterInvocations.Should().Be(1, "the filter is evaluated exactly once per admitted document");
            monitored.Verify(c => c.PatchItemAsync<JsonElement>(
                    It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                    It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "a single admission evaluation lets the genuine pending document drain and stamp delivered — it is not wedged by the non-idempotent filter");
        }

        // R4-STEP-001 no-wedge B: an AdditionalPendingFilter that THROWS on a second evaluation must never get a second
        // evaluation. Under single-by-construction admission the relay evaluates the filter exactly once, so the genuine
        // pending document drains and stamps delivered without the throw ever occurring. Before the fix the host gate's
        // first evaluation passed and the relay's second evaluation threw, propagating out and wedging the feed.
        [Fact]
        public async Task MustNotReEvaluateAFilterThatThrowsOnASecondCall()
        {
            var filterInvocations = 0;

            using ServiceProvider root = new ServiceCollection().BuildServiceProvider();

            Mock<Container> monitored = RecordingMonitoredContainer();
            Container lease = PhysicalContainer("shop", "orders-leases");
            CosmosOutboxRelayOptions options = OptionsFor(monitored.Object, lease);
            options.AdditionalPendingFilter = _ =>
            {
                if (Interlocked.Increment(ref filterInvocations) > 1)
                {
                    throw new InvalidOperationException("the AdditionalPendingFilter must be evaluated exactly once per admitted document");
                }

                return true;
            };
            options.BodyResolverFactory = _ => NullResolvingResolver();

            var host = new StandaloneCosmosOutboxRelayHostedService(
                root,
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                options);

            JsonElement admitted = PendingOutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            using Stream batch = StreamOf($"{{\"Documents\":[{admitted.GetRawText()}]}}");

            Func<Task> act = () => host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, CancellationToken.None);

            await act.Should().NotThrowAsync("the filter is evaluated exactly once, so its throw-on-second-call path is never reached and the feed is not wedged");
            filterInvocations.Should().Be(1, "the filter is evaluated exactly once per admitted document");
            monitored.Verify(c => c.PatchItemAsync<JsonElement>(
                    It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                    It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "the genuine pending document drains and stamps delivered exactly once");
        }
    }
}
