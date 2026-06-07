using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Linq;
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

        // UnitOfWork<TContext> is internal to the EF module and cannot be referenced by type symbol from the
        // test assembly. Match its closed-generic ImplementationType by name plus the supplied context arg.
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

            var descriptor = FindDescriptor(builder, typeof(IBrokeredMessageInbox), typeof(BrokeredMessageInbox<TestDbContext>));

            descriptor.Should().NotBeNull();
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
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

            var descriptor = FindDescriptor(builder, typeof(IBrokeredMessageOutbox), typeof(BrokeredMessageOutbox<TestDbContext>));

            descriptor.Should().NotBeNull();
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
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

        private sealed class TestDbContext : DbContext
        {
            public TestDbContext(DbContextOptions options) : base(options) { }
        }
    }
}
