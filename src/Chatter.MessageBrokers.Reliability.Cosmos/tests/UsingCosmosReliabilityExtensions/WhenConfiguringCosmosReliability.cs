using Chatter.CQRS.Commands;
using Chatter.CQRS.Pipeline;
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

        // The CaptureBuilder seam runs AddChatterCqrs only — it does NOT run AddMessageBrokers, which is what registers
        // the core default IRouteBrokeredMessages. The Cosmos extension DECORATES that core default, so tests that
        // exercise the capture/decorate path seed a stand-in core default router first, exactly as AddMessageBrokers
        // would have. The stand-in is a Moq instance so the inner-default identity is assertable.
        private static IRouteBrokeredMessages SeedCoreDefaultRouter(CommandPipelineBuilder builder)
        {
            var coreDefault = Mock.Of<IRouteBrokeredMessages>();
            builder.Services.AddScoped(_ => coreDefault);
            return coreDefault;
        }

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

        [Fact]
        public void MustDecorateCoreRouterWithHandleGatedOutboxRouter()
        {
            var builder = CaptureBuilder(b =>
            {
                SeedCoreDefaultRouter(b);
                b.WithCosmosDocumentReliability<CreateOrder>("shop", "orders", "orders-leases", _ => new PartitionKey("pk"), "/tenantId");
            });

            using var provider = builder.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            scope.ServiceProvider.GetRequiredService<IRouteBrokeredMessages>().Should().BeOfType<HandleGatedOutboxRouter>();
        }

        [Fact]
        public async Task MustRouteNonParticipantDispatchThroughInnerDefaultRouterWithoutReachingCosmosOutbox()
        {
            // The core default is seeded BEFORE the Cosmos extension runs (as AddMessageBrokers would), so the extension's
            // capture-before-Replace picks it up as the inner default. The decorator must route to this inner default —
            // never the Cosmos outbox — because no document-tier batch is open (the surface handle is null for this
            // non-participant dispatch).
            IRouteBrokeredMessages coreDefault = null;
            var builder = CaptureBuilder(b =>
            {
                coreDefault = SeedCoreDefaultRouter(b);
                b.WithCosmosDocumentReliability<CreateOrder>("shop", "orders", "orders-leases", _ => new PartitionKey("pk"), "/tenantId");
            });

            using var provider = builder.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var router = scope.ServiceProvider.GetRequiredService<IRouteBrokeredMessages>();
            router.Should().BeOfType<HandleGatedOutboxRouter>();

            var message = new OutboundBrokeredMessage("msg-1", new byte[] { 1 }, new System.Collections.Generic.Dictionary<string, object>(), "destination", new JsonBodyConverter());

            // No active handle -> must NOT throw (the bug: it threw from CosmosBrokeredMessageOutbox on a null handle).
            Func<Task> act = () => router.Route(message, transactionContext: null);
            await act.Should().NotThrowAsync();

            // The dispatch reached the seeded core default, not the Cosmos outbox.
            Mock.Get(coreDefault).Verify(r => r.Route(message, null), Times.Once);
        }

        [Fact]
        public void MustWrapTheCoreRouterExactlyOnceAcrossMultipleParticipationCalls()
        {
            var builder = CaptureBuilder(b =>
            {
                SeedCoreDefaultRouter(b);
                b.WithCosmosDocumentReliability<CreateOrder>("shop", "orders", "orders-leases", _ => new PartitionKey("pk"), "/tenantId");
                b.WithCosmosDocumentReliability<PostLedgerEntry>("fin", "ledger", "ledger-leases", _ => new PartitionKey("pk"), "/accountId");
            });

            // Exactly one IRouteBrokeredMessages descriptor remains (no double-wrap), and exactly one idempotency marker.
            builder.Services.Count(d => d.ServiceType == typeof(IRouteBrokeredMessages)).Should().Be(1);
            builder.Services.Count(d => d.ServiceType == typeof(HandleGatedRouterMarker)).Should().Be(1);

            // The wrap is the decorator over the core default; resolving the decorator must succeed (the inner default is
            // the seeded core router, materialized from the captured descriptor — not a second decorator).
            using var provider = builder.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IRouteBrokeredMessages>().Should().BeOfType<HandleGatedOutboxRouter>();
        }

        [Fact]
        public void MustFailLoudlyWhenNoCoreDefaultRouterWasRegisteredBeforeDecorating()
        {
            // No core default IRouteBrokeredMessages is seeded (AddMessageBrokers was never called). The decorator factory
            // must throw a clear named error rather than silently routing non-participants into the Cosmos outbox.
            var builder = ConfigureOne();

            using var provider = builder.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            Action act = () => scope.ServiceProvider.GetRequiredService<IRouteBrokeredMessages>();
            act.Should().Throw<InvalidOperationException>().WithMessage("*AddMessageBrokers*");
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
                _ => new PartitionKey("pk"),
                "/tenantId"));

            using var provider = builder.Services.BuildServiceProvider();
            var registry = provider.GetRequiredService<DocumentReliabilityRegistry>();
            registry.TryGet(typeof(CreateOrder), out var registration).Should().BeTrue();
            registration.DocumentContainerFactory.Should().NotBeNull();
            registration.LeaseContainerFactory.Should().NotBeNull();

            var factory = provider.GetRequiredService<CosmosContainerFactory>();
            factory.GetDocumentContainer(registration).Should().BeSameAs(document);
            factory.GetLeaseContainer(registration).Should().BeSameAs(lease);
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
