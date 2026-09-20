using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingReliabilityPipelineExtensions
{
    public class WhenConfiguringReliabilityBehaviors : Testing.Core.Context
    {
        // The reliability extensions deepen the CQRS command pipeline by Replace-registering
        // reliability services keyed to a consumer-supplied DbContext. CommandPipelineBuilder's
        // constructor is internal, so a real builder is captured through the public AddChatterCqrs seam.
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

        // Replace<TService,TImpl> is RemoveAll(TService)+Add, so the last descriptor for a service type
        // is authoritative. Match on BOTH ServiceType and ImplementationType.
        private static ServiceDescriptor FindDescriptor(CommandPipelineBuilder builder, Type serviceType, Type implementationType)
            => builder.Services.LastOrDefault(descriptor =>
                descriptor.ServiceType == serviceType && descriptor.ImplementationType == implementationType);

        // The split interfaces are forwarded to the shared concrete store/inbox via a factory, so the descriptor
        // carries an ImplementationFactory rather than an ImplementationType. Match on ServiceType alone for those.
        private static ServiceDescriptor FindForwardedDescriptor(CommandPipelineBuilder builder, Type serviceType)
            => builder.Services.LastOrDefault(descriptor =>
                descriptor.ServiceType == serviceType && descriptor.ImplementationFactory != null);

        // UnitOfWork<TContext> is internal to the EF module, but this assembly holds an InternalsVisibleTo
        // grant and CAN reference it by type symbol - WhenBindingReliabilityContext asserts directly against
        // typeof(UnitOfWork<PrimaryDbContext>). This helper takes its context as a runtime Type rather than a
        // type parameter, so it matches the closed-generic ImplementationType by name plus that context arg.
        private static ServiceDescriptor FindUnitOfWorkDescriptor(CommandPipelineBuilder builder, Type contextType)
            => builder.Services.LastOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IUnitOfWork) &&
                descriptor.ImplementationType != null &&
                descriptor.ImplementationType.IsGenericType &&
                descriptor.ImplementationType.Name == "UnitOfWork`1" &&
                descriptor.ImplementationType.GetGenericArguments().Single() == contextType);

        [Fact]
        public void MustRegisterScopedUnitOfWorkForUnitOfWorkBehavior()
        {
            var builder = CaptureBuilder(b => b.WithUnitOfWorkBehavior<TestDbContext>());

            var descriptor = FindUnitOfWorkDescriptor(builder, typeof(TestDbContext));

            descriptor.Should().NotBeNull();
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustReturnSameBuilderFromUnitOfWorkBehavior()
        {
            CommandPipelineBuilder captured = null;
            CommandPipelineBuilder returned = null;
            var services = new ServiceCollection();
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), builder =>
            {
                captured = builder;
                returned = builder.WithUnitOfWorkBehavior<TestDbContext>();
            });

            returned.Should().BeSameAs(captured);
        }

        [Fact]
        public void MustRegisterScopedInboxForInboxBehavior()
        {
            var builder = CaptureBuilder(b => b.WithInboxBehavior<TestDbContext>());

            var concrete = FindDescriptor(builder, typeof(BrokeredMessageInbox<TestDbContext>), typeof(BrokeredMessageInbox<TestDbContext>));
            var forwarded = FindForwardedDescriptor(builder, typeof(IBrokeredMessageInbox));

            concrete.Should().NotBeNull();
            concrete.Lifetime.Should().Be(ServiceLifetime.Scoped);
            forwarded.Should().NotBeNull();
            forwarded.Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterScopedUnitOfWorkViaInboxBehaviorDelegation()
        {
            var builder = CaptureBuilder(b => b.WithInboxBehavior<TestDbContext>());

            var descriptor = FindUnitOfWorkDescriptor(builder, typeof(TestDbContext));

            descriptor.Should().NotBeNull();
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustReturnSameBuilderFromInboxBehavior()
        {
            CommandPipelineBuilder captured = null;
            CommandPipelineBuilder returned = null;
            var services = new ServiceCollection();
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), builder =>
            {
                captured = builder;
                returned = builder.WithInboxBehavior<TestDbContext>();
            });

            returned.Should().BeSameAs(captured);
        }

        [Fact]
        public void MustRegisterScopedOutboxForOutboxProcessingBehavior()
        {
            var builder = CaptureBuilder(b => b.WithOutboxProcessingBehavior<TestDbContext>());

            var concrete = FindDescriptor(builder, typeof(BrokeredMessageOutbox<TestDbContext>), typeof(BrokeredMessageOutbox<TestDbContext>));
            var forwarded = FindForwardedDescriptor(builder, typeof(IBrokeredMessageOutbox));

            concrete.Should().NotBeNull();
            concrete.Lifetime.Should().Be(ServiceLifetime.Scoped);
            forwarded.Should().NotBeNull();
            forwarded.Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterScopedOutboxRouterForOutboxProcessingBehavior()
        {
            var builder = CaptureBuilder(b => b.WithOutboxProcessingBehavior<TestDbContext>());

            var descriptor = FindDescriptor(builder, typeof(IRouteBrokeredMessages), typeof(OutboxBrokeredMessageRouter));

            descriptor.Should().NotBeNull();
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterScopedUnitOfWorkViaOutboxProcessingBehaviorDelegation()
        {
            var builder = CaptureBuilder(b => b.WithOutboxProcessingBehavior<TestDbContext>());

            var descriptor = FindUnitOfWorkDescriptor(builder, typeof(TestDbContext));

            descriptor.Should().NotBeNull();
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustReturnSameBuilderFromOutboxProcessingBehavior()
        {
            CommandPipelineBuilder captured = null;
            CommandPipelineBuilder returned = null;
            var services = new ServiceCollection();
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), builder =>
            {
                captured = builder;
                returned = builder.WithOutboxProcessingBehavior<TestDbContext>();
            });

            returned.Should().BeSameAs(captured);
        }

        // Retention options are registered as a singleton INSTANCE, so the descriptor carries the finalized
        // object and no provider has to be built to read what an operator configured.
        private static EntityFrameworkReliabilityOptions FindRetentionOptions(CommandPipelineBuilder builder)
            => builder.Services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(EntityFrameworkReliabilityOptions))
                      ?.ImplementationInstance as EntityFrameworkReliabilityOptions;

        private static int CountPurgeServiceDescriptors(CommandPipelineBuilder builder)
            => builder.Services.Count(descriptor =>
                !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(ReliabilityRetentionPurgeService<TestDbContext>));

        [Fact]
        public void MustRegisterDisabledRetentionOptionsForInboxBehavior()
        {
            var builder = CaptureBuilder(b => b.WithInboxBehavior<TestDbContext>());

            var options = FindRetentionOptions(builder);

            options.Should().NotBeNull("the inbox resolves these options whatever order the reliability extensions were called in");
            options.InboxDeduplicationWindow.Should().BeNull("inbox retention is disabled until an operator opts in");
            options.ProcessedOutboxRetention.Should().BeNull("outbox retention is disabled until an operator opts in");
            options.PurgeInterval.Should().Be(TimeSpan.FromMinutes(5));
        }

        [Fact]
        public void MustRegisterDisabledRetentionOptionsForOutboxProcessingBehavior()
        {
            var builder = CaptureBuilder(b => b.WithOutboxProcessingBehavior<TestDbContext>());

            var options = FindRetentionOptions(builder);

            options.Should().NotBeNull();
            options.InboxDeduplicationWindow.Should().BeNull("inbox retention is disabled until an operator opts in");
            options.ProcessedOutboxRetention.Should().BeNull("outbox retention is disabled until an operator opts in");
        }

        [Fact]
        public void MustRegisterNoPurgeServiceWithoutAReliabilityRetentionCall()
        {
            var builder = CaptureBuilder(b =>
            {
                b.WithInboxBehavior<TestDbContext>();
                b.WithOutboxProcessingBehavior<TestDbContext>();
            });

            CountPurgeServiceDescriptors(builder).Should().Be(0, "seeding the default options must not start a purge nobody asked for");
        }

        [Fact]
        public void MustReplaceRetentionOptionsWithTheLastRetentionCall()
        {
            var builder = CaptureBuilder(b =>
            {
                b.WithReliabilityRetention<TestDbContext>(o => o.InboxDeduplicationWindow = TimeSpan.FromDays(1));
                b.WithReliabilityRetention<TestDbContext>(o => o.InboxDeduplicationWindow = TimeSpan.FromDays(7));
            });

            var options = FindRetentionOptions(builder);

            options.InboxDeduplicationWindow.Should().Be(TimeSpan.FromDays(7));
            builder.Services.Count(descriptor => descriptor.ServiceType == typeof(EntityFrameworkReliabilityOptions))
                   .Should().Be(1, "the last call replaces the registration rather than appending a second one");
        }

        [Fact]
        public void MustKeepConfiguredRetentionWhenABehaviorIsRegisteredAfterwards()
        {
            var builder = CaptureBuilder(b =>
            {
                b.WithReliabilityRetention<TestDbContext>(o => o.ProcessedOutboxRetention = TimeSpan.FromDays(3));
                b.WithInboxBehavior<TestDbContext>();
                b.WithOutboxProcessingBehavior<TestDbContext>();
            });

            FindRetentionOptions(builder).ProcessedOutboxRetention
                .Should().Be(TimeSpan.FromDays(3), "the default seeded by a behavior must never overwrite what an operator configured");
        }

        [Fact]
        public void MustRegisterPurgeServiceExactlyOnceAcrossRepeatedRetentionCalls()
        {
            var builder = CaptureBuilder(b =>
            {
                b.WithReliabilityRetention<TestDbContext>(o => o.InboxDeduplicationWindow = TimeSpan.FromDays(1));
                b.WithReliabilityRetention<TestDbContext>(o => o.InboxDeduplicationWindow = TimeSpan.FromDays(2));
                b.WithReliabilityRetention<TestDbContext>(o => o.InboxDeduplicationWindow = TimeSpan.FromDays(3));
            });

            CountPurgeServiceDescriptors(builder).Should().Be(1, "a second purge loop over the same context would double the delete traffic");
        }

        [Fact]
        public void MustReturnSameBuilderFromReliabilityRetention()
        {
            CommandPipelineBuilder captured = null;
            CommandPipelineBuilder returned = null;
            var services = new ServiceCollection();
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), builder =>
            {
                captured = builder;
                returned = builder.WithReliabilityRetention<TestDbContext>(o => o.InboxDeduplicationWindow = TimeSpan.FromDays(1));
            });

            returned.Should().BeSameAs(captured);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(-1L)]
        public void MustRefuseNonPositiveInboxDeduplicationWindow(long ticks)
        {
            Action configure = () => CaptureBuilder(b =>
                b.WithReliabilityRetention<TestDbContext>(o => o.InboxDeduplicationWindow = TimeSpan.FromTicks(ticks)));

            configure.Should().Throw<ArgumentOutOfRangeException>()
                     .WithMessage("*InboxDeduplicationWindow*", "a window that names no time would purge every inbox marker on the first pass");
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(-1L)]
        public void MustRefuseNonPositiveProcessedOutboxRetention(long ticks)
        {
            Action configure = () => CaptureBuilder(b =>
                b.WithReliabilityRetention<TestDbContext>(o => o.ProcessedOutboxRetention = TimeSpan.FromTicks(ticks)));

            configure.Should().Throw<ArgumentOutOfRangeException>()
                     .WithMessage("*ProcessedOutboxRetention*");
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(-1L)]
        public void MustRefuseNonPositivePurgeInterval(long ticks)
        {
            Action configure = () => CaptureBuilder(b =>
                b.WithReliabilityRetention<TestDbContext>(o => o.PurgeInterval = TimeSpan.FromTicks(ticks)));

            configure.Should().Throw<ArgumentOutOfRangeException>()
                     .WithMessage("*PurgeInterval*", "a non-positive interval spins the purge loop against the database with no wait");
        }

        // The scheduler's ceiling, measured on both target frameworks rather than quoted: Task.Delay accepts
        // TimeSpan.FromMilliseconds(uint.MaxValue - 1) and throws ArgumentOutOfRangeException(paramName: "delay")
        // one millisecond above it, identically on net8.0 and net10.0 (observed). These two facts hold the
        // registration bound to exactly that measurement from both sides, so neither a bound that drifts wide nor
        // one that drifts narrow can pass: raising MaxSchedulablePurgeInterval by one millisecond reddens
        // MustRefuseAPurgeIntervalTheSchedulerCannotSchedule alone, and lowering it by one tick reddens
        // MustAcceptTheLargestPurgeIntervalTheSchedulerCanSchedule alone.
        [Fact]
        public void MustRefuseAPurgeIntervalTheSchedulerCannotSchedule()
        {
            Action configure = () => CaptureBuilder(b =>
                b.WithReliabilityRetention<TestDbContext>(o => o.PurgeInterval = TimeSpan.FromMilliseconds((double)uint.MaxValue)));

            configure.Should().Throw<ArgumentOutOfRangeException>()
                     .WithMessage("*PurgeInterval*", "an interval Task.Delay refuses faults the first wait, and ExecuteAsync catches only cancellation, so the hosted service would end and retention would stop");
        }

        [Fact]
        public void MustAcceptTheLargestPurgeIntervalTheSchedulerCanSchedule()
        {
            Action configure = () => CaptureBuilder(b =>
                b.WithReliabilityRetention<TestDbContext>(o => o.PurgeInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1)));

            configure.Should().NotThrow("the registration bound is the scheduler's own ceiling and must admit every interval the scheduler accepts");
        }

        // Both retention windows are read only by subtracting them from DateTime.UtcNow - in
        // BrokeredMessageInbox.HasBeenReceived and HasMarkerExpired, and once per window in
        // ReliabilityRetentionPurgeService.PurgeOnceAsync - and that subtraction throws when the result would
        // precede DateTime.MinValue. The refusal is what keeps such a window away from all four sites, so
        // deleting the InboxDeduplicationWindow ceiling reddens the inbox fact alone and deleting the
        // ProcessedOutboxRetention ceiling reddens the outbox fact alone.
        [Fact]
        public void MustRefuseAnInboxDeduplicationWindowNoCutoffCanBeDerivedFrom()
        {
            Action configure = () => CaptureBuilder(b =>
                b.WithReliabilityRetention<TestDbContext>(o => o.InboxDeduplicationWindow = TimeSpan.MaxValue));

            configure.Should().Throw<ArgumentOutOfRangeException>()
                     .WithMessage("*InboxDeduplicationWindow*", "a window no cutoff can be derived from makes every receive and every purge pass throw instead of deduplicating");
        }

        [Fact]
        public void MustRefuseAProcessedOutboxRetentionNoCutoffCanBeDerivedFrom()
        {
            Action configure = () => CaptureBuilder(b =>
                b.WithReliabilityRetention<TestDbContext>(o => o.ProcessedOutboxRetention = TimeSpan.MaxValue));

            configure.Should().Throw<ArgumentOutOfRangeException>()
                     .WithMessage("*ProcessedOutboxRetention*", "a window no cutoff can be derived from makes every purge pass throw instead of reclaiming rows");
        }

        // The ceiling is the whole distance back to DateTime.MinValue, so a window far longer than any operator
        // would configure still registers. This is what separates refusing the unsubtractable from capping
        // retention at some invented duration: widening the refusal to reject, say, anything over a year reddens
        // here and nowhere else.
        [Fact]
        public void MustAcceptARetentionWindowACutoffCanStillBeDerivedFrom()
        {
            Action configure = () => CaptureBuilder(b => b.WithReliabilityRetention<TestDbContext>(o =>
            {
                o.InboxDeduplicationWindow = TimeSpan.FromDays(365_000);
                o.ProcessedOutboxRetention = TimeSpan.FromDays(365_000);
            }));

            configure.Should().NotThrow("a thousand-year window is still subtractable from UtcNow, and retention duration is the operator's to choose");
        }

        // The outbox poll is capped at ReliabilityOptions.OutboxPollBatchSize only on a SECOND constructor, so the
        // cap only reaches a running host if the container activates that constructor rather than the uncapped one.
        // This resolves the outbox the way a Chatter host does - through the registration WithOutboxProcessingBehavior
        // makes, over a published ReliabilityOptions - and watches the poll obey the configured batch.
        [Fact]
        public async Task MustResolveOutboxThroughTheBatchSizeAwareConstructor()
        {
            using var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped(_ => new TestDbContext(
                new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection).Options));
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), builder => builder.WithOutboxProcessingBehavior<TestDbContext>());
            ReliabilityOptionsBuilder.Create(services).WithOutboxPollBatchSize(2).Build();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await context.Database.EnsureCreatedAsync();

            for (var sequence = 0; sequence < 3; sequence++)
            {
                context.Set<OutboxMessage>().Add(CreateUnprocessedOutboxMessage($"message-{sequence}"));
            }

            await context.SaveChangesAsync();

            var outbox = (IPollableOutboxStore)scope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>();
            var polled = await outbox.GetUnprocessedMessagesFromOutbox();

            polled.Should().HaveCount(2, "the container must activate the ReliabilityOptions-aware constructor, so the poll takes the configured Outbox Poll Batch rather than the whole backlog");
        }

        private static OutboxMessage CreateUnprocessedOutboxMessage(string messageId)
            => new OutboxMessage
            {
                MessageId = messageId,
                Destination = "test-destination",
                MessageContext = "{}",
                MessageBody = "{}",
                MessageContentType = "application/json",
                SentToOutboxAtUtc = DateTime.UtcNow,
                ProcessedFromOutboxAtUtc = null,
                BatchId = Guid.NewGuid()
            };

        private sealed class TestDbContext : DbContext
        {
            public TestDbContext(DbContextOptions options) : base(options) { }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
                modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
            }
        }
    }
}
