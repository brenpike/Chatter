using Chatter.CQRS.Commands;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingReliabilityPipelineExtensions
{
    // Characterization tests: these record the behavior sequence the reliability extensions actually
    // produce for each call order. CommandBehaviorPipeline composes last-to-first, so the FIRST type in
    // the resolved sequence is the OUTERMOST behavior at execution time.
    public class WhenOrderingReliabilityBehaviors : Testing.Core.Context
    {
        // Resolution is a pure DI exercise: the reliability collaborators are stubbed AFTER the pipeline is
        // configured so the last descriptor for each of them wins, and no DbContext or relational provider is
        // ever constructed.
        private static IReadOnlyList<Type> ResolveBehaviorSequence(Action<CommandPipelineBuilder> configure)
        {
            var services = new ServiceCollection();
            services.AddChatterCqrs(Mock.Of<IConfiguration>(), configure);
            services.AddLogging();
            services.AddScoped(_ => Mock.Of<IUnitOfWork>());
            services.AddScoped(_ => Mock.Of<IBrokeredMessageInbox>());
            services.AddScoped(_ => Mock.Of<IOutboxProcessor>());

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            return scope.ServiceProvider
                .GetRequiredService<IEnumerable<ICommandBehavior<TestCommand>>>()
                .Select(behavior => behavior.GetType())
                .ToList();
        }

        [Fact]
        public void MustOrderBehaviorsForInboxThenOutboxCallOrder()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithInboxBehavior<TestDbContext>();
                builder.WithOutboxProcessingBehavior<TestDbContext>();
            });

            sequence.Should().Equal(
                typeof(InboxBehavior<TestCommand>),
                typeof(OutboxProcessingBehavior<TestCommand>),
                typeof(UnitOfWorkBehavior<TestCommand>));
        }

        [Fact]
        public void MustOrderBehaviorsForOutboxThenInboxCallOrder()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithOutboxProcessingBehavior<TestDbContext>();
                builder.WithInboxBehavior<TestDbContext>();
            });

            sequence.Should().Equal(
                typeof(OutboxProcessingBehavior<TestCommand>),
                typeof(UnitOfWorkBehavior<TestCommand>),
                typeof(InboxBehavior<TestCommand>));
        }

        private sealed class TestCommand : ICommand
        {
        }

        private sealed class TestDbContext : DbContext
        {
            public TestDbContext(DbContextOptions options) : base(options) { }
        }
    }
}
