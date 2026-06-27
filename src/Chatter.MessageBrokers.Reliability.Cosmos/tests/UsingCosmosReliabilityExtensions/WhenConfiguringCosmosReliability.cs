using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityExtensions
{
    public class WhenConfiguringCosmosReliability : Testing.Core.Context
    {
        private sealed class CreateOrder : ICommand { }
        private sealed class PostLedgerEntry : ICommand { }

        // CommandPipelineBuilder's constructor is internal, so a real builder is captured through the public
        // AddChatterCqrs seam — same precedent as the EF reliability extension tests.
        private static CommandPipelineBuilder CaptureBuilder(Action<CommandPipelineBuilder> configure)
        {
            CommandPipelineBuilder captured = null;
            var services = new ServiceCollection();
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), builder =>
            {
                captured = builder;
                configure(builder);
            });

            return captured;
        }

        private static CommandPipelineBuilder ConfigureOne()
            => CaptureBuilder(b => b.WithCosmosDocumentReliability<CreateOrder>(
                database: "shop",
                container: "orders",
                lease: "orders-leases",
                resolver: _ => new PartitionKey("pk"),
                "/tenantId"));

        [Fact]
        public void MustRegisterCommandIntoRegistryViaGenericApi()
        {
            var builder = ConfigureOne();

            using var provider = builder.Services.BuildServiceProvider();
            var registry = provider.GetRequiredService<DocumentReliabilityRegistry>();

            registry.TryGet(typeof(CreateOrder), out var registration).Should().BeTrue();
            registration.Database.Should().Be("shop");
            registration.ContainerName.Should().Be("orders");
            registration.LeaseName.Should().Be("orders-leases");
            registration.PartitionKeyPath.Should().ContainSingle().Which.Should().Be("/tenantId");
            registration.Resolver.Should().NotBeNull();
        }

        [Fact]
        public void MustRegisterContainerFactorySingleton()
        {
            var builder = ConfigureOne();

            using var provider = builder.Services.BuildServiceProvider();
            provider.GetRequiredService<CosmosContainerFactory>().Should().NotBeNull();
        }

        [Fact]
        public void MustRegisterDocumentTierReliabilitySurface()
        {
            var builder = ConfigureOne();

            using var provider = builder.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var surface = scope.ServiceProvider.GetRequiredService<IDocumentTierReliabilitySurface>();

            surface.Should().NotBeNull();
            surface.CurrentHandle.Should().BeNull();
        }

        [Fact]
        public void MustRegisterCosmosOutbox()
        {
            var builder = ConfigureOne();

            using var provider = builder.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            scope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>().Should().BeOfType<CosmosBrokeredMessageOutbox>();
        }

        // --- Registration-order independence (#239) -------------------------------------------------------------------
        //
        // The router wiring constructs the inner-default arm DIRECTLY as
        // new BrokeredMessageRouter(IMessagingInfrastructureProvider) — never captures the core default descriptor — so
        // the decorator resolves and a non-participant dispatch routes to the broker regardless of whether
        // AddChatterCqrs (which runs the Cosmos pipeline callback) or AddMessageBrokers runs first, and regardless of
        // RouteMessagesToOutbox. These tests exercise BOTH orders x BOTH RouteMessagesToOutbox states over a shared
        // service collection, replacing the always-registered IMessagingInfrastructureProvider with a mock so the broker
        // dispatch is assertable, and replacing IBrokeredMessageOutbox with a spy so the inner-default arm can be proven
        // never to reach the outbox. No live CosmosClient is required.

        private static OutboundBrokeredMessage NonParticipantMessage()
            => new OutboundBrokeredMessage(
                "msg-1",
                new byte[] { 1 },
                new System.Collections.Generic.Dictionary<string, object>(),
                "destination",
                new JsonBodyConverter());

        // Wires AddChatterCqrs (which runs the Cosmos pipeline callback) and AddMessageBrokers in the requested order on
        // ONE shared service collection, then replaces the broker provider with a mock dispatcher and the outbox with a
        // spy so the routing assertions can run without any live Cosmos/transport infrastructure.
        private static (IServiceCollection services, Mock<IMessagingInfrastructureDispatcher> dispatcher, Mock<IBrokeredMessageOutbox> outbox)
            WireBothModules(bool cqrsFirst, bool routeMessagesToOutbox)
        {
            var services = new ServiceCollection();

            Action<CommandPipelineBuilder> cosmosCallback = pipeline => pipeline.WithCosmosDocumentReliability<CreateOrder>(
                database: "shop",
                container: "orders",
                lease: "orders-leases",
                resolver: _ => new PartitionKey("pk"),
                "/tenantId");

            Action<MessageBrokerOptionsBuilder> brokerOptions = routeMessagesToOutbox
                ? options => options.AddReliabilityOptions(r => r.WithOutboxRouting())
                : (Action<MessageBrokerOptionsBuilder>)null;

            if (cqrsFirst)
            {
                // AddChatterCqrs runs the Cosmos Replace<IRouteBrokeredMessages> synchronously in its pipeline callback,
                // BEFORE AddMessageBrokers registers the core default router. The buggy capture-the-descriptor approach
                // observed a null descriptor here and threw; the direct-construction approach does not.
                IChatterBuilder chatter = services.AddChatterCqrs(Mock.Of<IConfiguration>(), cosmosCallback);
                chatter.AddMessageBrokers(brokerOptions);
            }
            else
            {
                // AddMessageBrokers registers the core default router FIRST; the Cosmos pipeline callback then Replaces it
                // with the handle-gated decorator over the directly-constructed broker arm.
                IChatterBuilder chatter = services.AddChatterCqrs(Mock.Of<IConfiguration>());
                chatter.AddMessageBrokers(brokerOptions);
                // AddCommandPipeline is the public IChatterBuilder seam for adding a pipeline AFTER the broker module has
                // registered its default router — exactly the reverse-order case this test exercises. The obsolete
                // warning is suppressed locally; there is no non-obsolete overload that runs a pipeline callback against
                // an already-constructed IChatterBuilder.
#pragma warning disable CS0618
                chatter.AddCommandPipeline(cosmosCallback);
#pragma warning restore CS0618
            }

            // The decorator factory eagerly resolves IBrokeredMessageOutbox (to build the cosmos arm) and resolves
            // IMessagingInfrastructureProvider lazily inside the inner-default arm at dispatch time. Replace both with
            // test doubles so the router resolves and dispatches without a live CosmosClient or transport. Replace is
            // order-agnostic (RemoveAll + Add), so it overrides whatever each module registered.
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            services.Replace<IMessagingInfrastructureProvider>(ServiceLifetime.Singleton, _ => provider.Object);

            var outbox = new Mock<IBrokeredMessageOutbox>();
            services.Replace<IBrokeredMessageOutbox>(ServiceLifetime.Scoped, _ => outbox.Object);

            return (services, dispatcher, outbox);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        public void MustResolveHandleGatedRouterRegardlessOfRegistrationOrderOrOutboxRouting(bool cqrsFirst, bool routeMessagesToOutbox)
        {
            var (services, _, _) = WireBothModules(cqrsFirst, routeMessagesToOutbox);

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            scope.ServiceProvider.GetRequiredService<IRouteBrokeredMessages>().Should().BeOfType<HandleGatedOutboxRouter>();
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        public async Task MustRouteNonParticipantDispatchToBrokerWithoutReachingOutboxRegardlessOfOrderOrOutboxRouting(bool cqrsFirst, bool routeMessagesToOutbox)
        {
            var (services, dispatcher, outbox) = WireBothModules(cqrsFirst, routeMessagesToOutbox);

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var router = scope.ServiceProvider.GetRequiredService<IRouteBrokeredMessages>();
            router.Should().BeOfType<HandleGatedOutboxRouter>();

            var message = NonParticipantMessage();

            // No participant batch is open (the surface handle is null), so the non-participant dispatch must NOT throw
            // (the bug: it threw — either on a null captured descriptor, or, under RouteMessagesToOutbox, from the Cosmos
            // outbox on a null handle).
            Func<Task> act = () => router.Route(message, transactionContext: null);
            await act.Should().NotThrowAsync();

            // The dispatch reached the broker via the always-registered infrastructure provider — never the outbox.
            // (The single-message SendToOutbox is a default interface method that delegates to the batch overload, so the
            // batch-overload assertion covers both entry points.)
            dispatcher.Verify(d => d.Dispatch(message, null), Times.Once);
            outbox.Verify(
                o => o.SendToOutbox(
                    It.IsAny<System.Collections.Generic.IEnumerable<OutboundBrokeredMessage>>(),
                    It.IsAny<TransactionContext>(),
                    It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public void MustRegisterExactlyOneRouterDescriptorAcrossMultipleParticipationCalls()
        {
            var builder = CaptureBuilder(b =>
            {
                b.WithCosmosDocumentReliability<CreateOrder>("shop", "orders", "orders-leases", _ => new PartitionKey("pk"), "/tenantId");
                b.WithCosmosDocumentReliability<PostLedgerEntry>("fin", "ledger", "ledger-leases", _ => new PartitionKey("pk"), "/accountId");
            });

            // Each call Replaces IRouteBrokeredMessages with the same factory shape (RemoveAll + Add), so exactly one
            // descriptor — the decorator — remains, never double-wrapped.
            builder.Services.Count(d => d.ServiceType == typeof(IRouteBrokeredMessages)).Should().Be(1);
        }

        [Fact]
        public void MustRegisterBatchLifecycleBehaviorAsOutermostBehavior()
        {
            var builder = ConfigureOne();

            // Behaviors are registered as open-generic ICommandBehavior<> descriptors; the CommandBehaviorPipeline
            // reverses the resolved sequence, so the FIRST-registered ICommandBehavior descriptor is the outermost.
            var firstBehavior = builder.Services.First(descriptor =>
                descriptor.ServiceType == typeof(ICommandBehavior<>));

            firstBehavior.ImplementationType.Should().Be(typeof(DocumentTierBatchLifecycleBehavior<>));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void MustRejectInvalidPartitionKeyPathSegment(string invalidSegment)
        {
            Action configure = () => CaptureBuilder(b => b.WithCosmosDocumentReliability<CreateOrder>(
                "shop", "orders", "orders-leases",
                _ => new PartitionKey("pk"),
                "/tenantId", invalidSegment));

            configure.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void MustRejectWhitespacePartitionKeySegmentOnBothRegistrationPaths()
        {
            // Both the command-pipeline registration and the standalone relay registration funnel the partition-key path
            // through the SAME hardened validator, so the SAME bad input is rejected identically on both paths.
            Action commandPipeline = () => CaptureBuilder(b => b.WithCosmosDocumentReliability<CreateOrder>(
                "shop", "orders", "orders-leases",
                _ => new PartitionKey("pk"),
                "/tenantId", "   "));

            Action standaloneRelay = () => new ServiceCollection().AddCosmosOutboxRelay(options =>
            {
                options.MonitoredContainerFactory = _ => Mock.Of<Container>();
                options.LeaseContainerFactory = _ => Mock.Of<Container>();
                options.PartitionKeyPath = new[] { "/tenantId", "   " };
            });

            commandPipeline.Should().Throw<ArgumentException>("the command-pipeline registration rejects a whitespace segment");
            standaloneRelay.Should().Throw<ArgumentException>("the standalone relay registration rejects the same whitespace segment through the shared validator");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void MustRejectInvalidDatabase(string invalidDatabase)
        {
            Action configure = () => CaptureBuilder(b => b.WithCosmosDocumentReliability<CreateOrder>(
                invalidDatabase, "orders", "orders-leases",
                _ => new PartitionKey("pk"),
                "/tenantId"));

            configure.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void MustNotBeAffectedByPostRegistrationMutationOfPartitionKeyPath()
        {
            var path = new[] { "/tenantId" };

            var builder = CaptureBuilder(b => b.WithCosmosDocumentReliability<CreateOrder>(
                "shop", "orders", "orders-leases",
                _ => new PartitionKey("pk"),
                path));

            // Mutating the caller-owned array after registration must not corrupt the registered path.
            path[0] = "/corrupted";

            using var provider = builder.Services.BuildServiceProvider();
            var registry = provider.GetRequiredService<DocumentReliabilityRegistry>();
            registry.TryGet(typeof(CreateOrder), out var registration).Should().BeTrue();
            registration.PartitionKeyPath.Should().ContainSingle().Which.Should().Be("/tenantId");
        }

        [Fact]
        public void MustRegisterInfrastructureIdempotentlyAcrossMultipleCalls()
        {
            var builder = CaptureBuilder(b =>
            {
                b.WithCosmosDocumentReliability<CreateOrder>("shop", "orders", "orders-leases", _ => new PartitionKey("pk"), "/tenantId");
                b.WithCosmosDocumentReliability<PostLedgerEntry>("fin", "ledger", "ledger-leases", _ => new PartitionKey("pk"), "/accountId");
            });

            // Both commands are additive in the single shared registry.
            using var provider = builder.Services.BuildServiceProvider();
            var registry = provider.GetRequiredService<DocumentReliabilityRegistry>();
            registry.TryGet(typeof(CreateOrder), out _).Should().BeTrue();
            registry.TryGet(typeof(PostLedgerEntry), out _).Should().BeTrue();

            // The registry is a single shared singleton instance, not one per call.
            builder.Services.Count(d => d.ServiceType == typeof(DocumentReliabilityRegistry)).Should().Be(1);
            // The container factory is registered once.
            builder.Services.Count(d => d.ServiceType == typeof(CosmosContainerFactory)).Should().Be(1);
            // The outermost behavior is registered exactly once even across two participation calls.
            builder.Services.Count(d =>
                d.ServiceType == typeof(ICommandBehavior<>)
                && d.ImplementationType == typeof(DocumentTierBatchLifecycleBehavior<>)).Should().Be(1);
        }

        [Fact]
        public void MustResolveContainersViaAdvancedFactoryOverload()
        {
            var document = Mock.Of<Container>();
            var lease = Mock.Of<Container>();
            var builder = CaptureBuilder(b => b.WithCosmosDocumentReliability<CreateOrder>(
                _ => document,
                _ => lease,
                monitoredSourceIdentity: "orders-source",
                leaseSourceIdentity: "orders-lease-source",
                _ => new PartitionKey("pk"),
                "/tenantId"));

            using var provider = builder.Services.BuildServiceProvider();
            var registry = provider.GetRequiredService<DocumentReliabilityRegistry>();
            registry.TryGet(typeof(CreateOrder), out var registration).Should().BeTrue();
            registration.DocumentContainerFactory.Should().NotBeNull();
            registration.LeaseContainerFactory.Should().NotBeNull();
            registration.DeclaredSourceIdentity.Should().NotBeNull();
            registration.DeclaredSourceIdentity.Value.Monitored.Should().Be("orders-source");
            registration.DeclaredSourceIdentity.Value.Lease.Should().Be("orders-lease-source");

            var factory = provider.GetRequiredService<CosmosContainerFactory>();
            factory.GetDocumentContainer(registration).Should().BeSameAs(document);
            factory.GetLeaseContainer(registration).Should().BeSameAs(lease);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void MustRejectInvalidMonitoredSourceIdentityOnAdvancedOverload(string invalidIdentity)
        {
            Action configure = () => CaptureBuilder(b => b.WithCosmosDocumentReliability<CreateOrder>(
                _ => Mock.Of<Container>(),
                _ => Mock.Of<Container>(),
                monitoredSourceIdentity: invalidIdentity,
                leaseSourceIdentity: "orders-lease-source",
                _ => new PartitionKey("pk"),
                "/tenantId"));

            configure.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void MustRejectInvalidLeaseSourceIdentityOnAdvancedOverload(string invalidIdentity)
        {
            Action configure = () => CaptureBuilder(b => b.WithCosmosDocumentReliability<CreateOrder>(
                _ => Mock.Of<Container>(),
                _ => Mock.Of<Container>(),
                monitoredSourceIdentity: "orders-source",
                leaseSourceIdentity: invalidIdentity,
                _ => new PartitionKey("pk"),
                "/tenantId"));

            configure.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void MustThrowOnDuplicateCommandTypeRegistration()
        {
            Action configure = () => CaptureBuilder(b =>
            {
                b.WithCosmosDocumentReliability<CreateOrder>("shop", "orders", "orders-leases", _ => new PartitionKey("pk"), "/tenantId");
                b.WithCosmosDocumentReliability<CreateOrder>("shop", "orders-v2", "orders-v2-leases", _ => new PartitionKey("pk"), "/tenantId");
            });

            configure.Should().Throw<InvalidOperationException>().WithMessage("*CreateOrder*");
        }
    }
}
