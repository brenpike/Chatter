using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityExtensions
{
    public class WhenConfiguringCosmosReliability : Testing.Core.Context
    {
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

        // Container is an abstract SDK class; Moq mocks it to satisfy injection without a live endpoint.
        private static (Container document, Container lease) MockContainers()
            => (Mock.Of<Container>(), Mock.Of<Container>());

        private static CommandPipelineBuilder ConfigureWith(Container document, Container lease)
            => CaptureBuilder(b => b.WithCosmosDocumentReliability(
                document,
                lease,
                _ => new PartitionKey("pk"),
                "/tenantId"));

        [Fact]
        public void MustResolveInjectedDocumentContainer()
        {
            var (document, lease) = MockContainers();
            var builder = ConfigureWith(document, lease);

            using var provider = builder.Services.BuildServiceProvider();
            var resolved = provider.GetRequiredService<DocumentContainer>();

            resolved.Container.Should().BeSameAs(document);
        }

        [Fact]
        public void MustResolveInjectedLeaseContainer()
        {
            var (document, lease) = MockContainers();
            var builder = ConfigureWith(document, lease);

            using var provider = builder.Services.BuildServiceProvider();
            var resolved = provider.GetRequiredService<LeaseContainer>();

            resolved.Container.Should().BeSameAs(lease);
        }

        [Fact]
        public void MustResolvePartitionKeyResolver()
        {
            var (document, lease) = MockContainers();
            var builder = ConfigureWith(document, lease);

            using var provider = builder.Services.BuildServiceProvider();
            var resolver = provider.GetRequiredService<PartitionKeyResolver>();

            resolver.Should().NotBeNull();
            resolver.PartitionKeyPath.Should().ContainSingle().Which.Should().Be("/tenantId");
            resolver.Resolve.Should().NotBeNull();
        }

        [Fact]
        public void MustResolveDocumentTierReliabilitySurface()
        {
            var (document, lease) = MockContainers();
            var builder = ConfigureWith(document, lease);

            using var provider = builder.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var surface = scope.ServiceProvider.GetRequiredService<IDocumentTierReliabilitySurface>();

            surface.Should().NotBeNull();
            surface.CurrentHandle.Should().BeNull();
        }

        [Fact]
        public void MustRegisterBatchLifecycleBehaviorAsOutermostBehavior()
        {
            var (document, lease) = MockContainers();
            var builder = ConfigureWith(document, lease);

            // Behaviors are registered as open-generic ICommandBehavior<> descriptors; the CommandBehaviorPipeline
            // reverses the resolved sequence, so the FIRST-registered ICommandBehavior descriptor is the outermost.
            var firstBehavior = builder.Services.First(descriptor =>
                descriptor.ServiceType == typeof(ICommandBehavior<>));

            firstBehavior.ImplementationType.Should().Be(typeof(DocumentTierBatchLifecycleBehavior<>));
        }

        [Fact]
        public void MustNotCreateAnyContainerOfItsOwn()
        {
            var (document, lease) = MockContainers();
            var builder = ConfigureWith(document, lease);

            using var provider = builder.Services.BuildServiceProvider();

            // The provider binds only the two injected instances; it resolves no bare Container of its own.
            provider.GetService<Container>().Should().BeNull();
            provider.GetRequiredService<DocumentContainer>().Container.Should().BeSameAs(document);
            provider.GetRequiredService<LeaseContainer>().Container.Should().BeSameAs(lease);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void MustRejectInvalidPartitionKeyPathSegment(string invalidSegment)
        {
            var (document, lease) = MockContainers();

            Action configure = () => CaptureBuilder(b => b.WithCosmosDocumentReliability(
                document,
                lease,
                _ => new PartitionKey("pk"),
                "/tenantId",
                invalidSegment));

            configure.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void MustNotBeAffectedByPostRegistrationMutationOfPartitionKeyPath()
        {
            var (document, lease) = MockContainers();
            var path = new[] { "/tenantId" };

            var builder = CaptureBuilder(b => b.WithCosmosDocumentReliability(
                document,
                lease,
                _ => new PartitionKey("pk"),
                path));

            // Mutating the caller-owned array after registration must not corrupt the registered path.
            path[0] = "/corrupted";

            using var provider = builder.Services.BuildServiceProvider();
            var resolver = provider.GetRequiredService<PartitionKeyResolver>();

            resolver.PartitionKeyPath.Should().ContainSingle().Which.Should().Be("/tenantId");
        }

        [Fact]
        public void MustResolveContainersViaFactoryOverload()
        {
            var (document, lease) = MockContainers();
            var builder = CaptureBuilder(b => b.WithCosmosDocumentReliability(
                _ => document,
                _ => lease,
                _ => new PartitionKey("pk"),
                "/tenantId"));

            using var provider = builder.Services.BuildServiceProvider();

            provider.GetRequiredService<DocumentContainer>().Container.Should().BeSameAs(document);
            provider.GetRequiredService<LeaseContainer>().Container.Should().BeSameAs(lease);
        }
    }
}
