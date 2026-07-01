using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Inbox;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosInboxServiceCollectionExtensions
{
    /// <summary>
    /// Characterizes the standalone Cosmos inbox DI surface (#253, ADR-0009):
    /// <see cref="CosmosInboxServiceCollectionExtensions.WithCosmosInbox"/> REPLACES <see cref="IBrokeredMessageInbox"/>
    /// with <see cref="CosmosBrokeredMessageInbox"/> and adds <c>InboxBehavior&lt;&gt;</c> — and registers NOTHING else
    /// (no hosted service, outbox, lease, router, or document-reliability registry). It eagerly validates the options.
    /// </summary>
    public class WhenRegisteringCosmosInbox : Testing.Core.Context
    {
        // CommandPipelineBuilder's constructor is internal, so a real builder is captured through the public AddChatterCqrs
        // seam — same precedent as the document-tier reliability extension tests.
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

        private static Action<CosmosInboxOptions> Valid(Action<CosmosInboxOptions> extra = null)
            => options =>
            {
                options.Database = "shop";
                options.Container = "idempotency";
                extra?.Invoke(options);
            };

        private static CosmosClient MockClient()
        {
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer(It.IsAny<string>(), It.IsAny<string>())).Returns(new Mock<Container>().Object);
            return client.Object;
        }

        [Fact]
        public void MustReplaceBrokeredMessageInboxWithTheCosmosInbox()
        {
            CommandPipelineBuilder builder = CaptureBuilder(b =>
            {
                // Pre-register a default inbox so the Replace (not add) semantics are observable.
                b.Services.AddScoped(_ => Mock.Of<IBrokeredMessageInbox>());
                b.WithCosmosInbox(Valid());
            });
            builder.Services.AddSingleton(MockClient());

            builder.Services.Where(d => d.ServiceType == typeof(IBrokeredMessageInbox)).Should().ContainSingle(
                "the Cosmos inbox REPLACES any pre-existing IBrokeredMessageInbox rather than stacking a second registration")
                .Which.Lifetime.Should().Be(ServiceLifetime.Scoped, "the Cosmos inbox is registered scoped (EF parity, ADR-0009 D6)");

            using ServiceProvider provider = builder.Services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IBrokeredMessageInbox>().Should().BeOfType<CosmosBrokeredMessageInbox>();
        }

        [Fact]
        public void MustAddTheInboxBehavior()
        {
            CommandPipelineBuilder builder = CaptureBuilder(b => b.WithCosmosInbox(Valid()));

            builder.Services.Should().Contain(
                d => d.ServiceType == typeof(ICommandBehavior<>) && d.ImplementationType == typeof(InboxBehavior<>),
                "WithCosmosInbox wires dedup through the existing InboxBehavior<> seam");
        }

        [Fact]
        public void MustRegisterNothingElseNoHostedServiceOutboxLeaseRouterOrRegistry()
        {
            CommandPipelineBuilder builder = CaptureBuilder(b => b.WithCosmosInbox(Valid()));

            builder.Services.Should().NotContain(d => d.ServiceType == typeof(IHostedService),
                "the standalone inbox registers no relay/lease hosted service");
            builder.Services.Should().NotContain(d => d.ServiceType == typeof(DocumentReliabilityRegistry),
                "the standalone inbox does not touch the document-reliability registry");
            builder.Services.Should().NotContain(d => d.ServiceType == typeof(CosmosContainerFactory),
                "the standalone inbox registers no document-tier container factory");
            builder.Services.Should().NotContain(d => d.ImplementationType == typeof(CosmosBrokeredMessageOutbox),
                "the standalone inbox registers no outbox");
        }

        [Fact]
        public void MustNotRequireACosmosClientAtRegistrationTime()
        {
            // The container is derived lazily inside the scoped factory, so registration itself must not resolve the client.
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid()));

            act.Should().NotThrow();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustRejectMissingDatabase(string database)
        {
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid(o => o.Database = database)));

            act.Should().Throw<ArgumentException>("a non-null, non-whitespace database is required");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustRejectMissingContainer(string container)
        {
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid(o => o.Container = container)));

            act.Should().Throw<ArgumentException>("a non-null, non-whitespace container is required");
        }

        [Fact]
        public void MustRejectAMultiSegmentPartitionKeyPath()
        {
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid(o => o.PartitionKeyPath = new[] { "/tenantId", "/idempotencyKey" })));

            act.Should().Throw<ArgumentException>("v1 supports only a single-segment partition-key path (hierarchical deferred to #254)");
        }

        [Fact]
        public void MustRejectAnEmptyPartitionKeyPath()
        {
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid(o => o.PartitionKeyPath = Array.Empty<string>())));

            act.Should().Throw<ArgumentException>("a non-empty partition-key path is required");
        }

        [Fact]
        public void MustRejectAReadBackBudgetBelowOneAttempt()
        {
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid(o => o.ReadBackMaxAttempts = 0)));

            act.Should().Throw<ArgumentException>("the confirm read-back must run at least once");
        }

        [Fact]
        public void MustRejectAPositiveMarkerTtlCombinedWithATtlRootedPartitionKeyPath()
        {
            // A positive MarkerTimeToLive stamps the Cosmos-reserved `ttl` field; a `/ttl` partition path would then have
            // its partition-value stamp overwrite that numeric TTL (corrupting it / defeating the dedup window). Reject
            // the combination fail-loud at registration, before any Cosmos write.
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid(o =>
            {
                o.PartitionKeyPath = new[] { "/ttl" };
                o.MarkerTimeToLive = 60;
            })));

            act.Should().Throw<ArgumentException>("a positive marker TTL collides with a /ttl-rooted partition-key path");
        }

        [Fact]
        public void MustAllowATtlRootedPartitionKeyPathWhenNoMarkerTtlIsConfigured()
        {
            // Without a positive MarkerTimeToLive the marker emits no ttl field, so a `/ttl` partition path has nothing to
            // collide with — it stays legal (parity with the document-tier marker, which never emits a ttl).
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid(o => o.PartitionKeyPath = new[] { "/ttl" })));

            act.Should().NotThrow();
        }

        [Fact]
        public void MustRejectANegativeReadBackInterval()
        {
            Action act = () => CaptureBuilder(b => b.WithCosmosInbox(Valid(o => o.ReadBackInterval = TimeSpan.FromMilliseconds(-1))));

            act.Should().Throw<ArgumentException>("the read-back interval must be non-negative");
        }

        [Fact]
        public void MustDefaultOptionsToTheDocumentedValues()
        {
            var options = new CosmosInboxOptions();

            options.Database.Should().BeNull();
            options.Container.Should().BeNull();
            options.PartitionKeyPath.Should().ContainSingle().Which.Should().Be("/idempotencyKey");
            options.MarkerTimeToLive.Should().BeNull();
            options.ReadBackMaxAttempts.Should().Be(5);
            options.ReadBackInterval.Should().Be(TimeSpan.FromMilliseconds(50));
        }
    }
}
