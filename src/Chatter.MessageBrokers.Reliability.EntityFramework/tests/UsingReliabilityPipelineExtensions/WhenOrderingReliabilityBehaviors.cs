using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
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
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingReliabilityPipelineExtensions
{
    // These tests pin the order-independence contract of the reliability extensions: whichever order the
    // inbox and outbox extensions are called in, and however many times the unit of work behavior is asked
    // for, the resolved behavior sequence is the same. CommandBehaviorPipeline composes last-to-first, so
    // the FIRST type in the resolved sequence is the OUTERMOST behavior at execution time: outbox
    // processing wraps the unit of work, which in turn wraps the inbox.
    public class WhenOrderingReliabilityBehaviors : Testing.Core.Context
    {
        private static readonly Type[] _orderIndependentSequence = new[]
        {
            typeof(OutboxProcessingBehavior<TestCommand>),
            typeof(UnitOfWorkBehavior<TestCommand>),
            typeof(InboxBehavior<TestCommand>)
        };

        // A behavior the application registers for itself between the two reliability extension calls keeps
        // the slot its own registration chose: the reliability behaviors are ordered relative to each other,
        // never hoisted past a behavior this package does not own.
        private static readonly Type[] _orderIndependentSequenceAroundApplicationBehavior = new[]
        {
            typeof(OutboxProcessingBehavior<TestCommand>),
            typeof(UnitOfWorkBehavior<TestCommand>),
            typeof(ApplicationBehavior<TestCommand>),
            typeof(InboxBehavior<TestCommand>)
        };

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
        public void MustOrderBehaviorsIndependentlyOfInboxThenOutboxCallOrder()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithInboxBehavior<TestDbContext>();
                builder.WithOutboxProcessingBehavior<TestDbContext>();
            });

            sequence.Should().Equal(_orderIndependentSequence);
        }

        [Fact]
        public void MustOrderBehaviorsIndependentlyOfOutboxThenInboxCallOrder()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithOutboxProcessingBehavior<TestDbContext>();
                builder.WithInboxBehavior<TestDbContext>();
            });

            sequence.Should().Equal(_orderIndependentSequence);
        }

        // An explicit third request for the unit of work behavior, on top of the two the inbox and outbox
        // extensions each make implicitly, must not move it: the contract holds for an arbitrary number of
        // callers, not just the two the reliability extensions happen to contribute today.
        [Fact]
        public void MustOrderBehaviorsIndependentlyOfRedundantUnitOfWorkRequests()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithOutboxProcessingBehavior<TestDbContext>();
                builder.WithInboxBehavior<TestDbContext>();
                builder.WithUnitOfWorkBehavior<TestDbContext>();
            });

            sequence.Should().Equal(_orderIndependentSequence);
        }

        [Fact]
        public void MustOrderBehaviorsIndependentlyOfInboxThenOutboxCallOrderAroundApplicationBehavior()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithInboxBehavior<TestDbContext>();
                builder.WithBehavior(typeof(ApplicationBehavior<>));
                builder.WithOutboxProcessingBehavior<TestDbContext>();
            });

            sequence.Should().Equal(_orderIndependentSequenceAroundApplicationBehavior);
        }

        [Fact]
        public void MustOrderBehaviorsIndependentlyOfOutboxThenInboxCallOrderAroundApplicationBehavior()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithOutboxProcessingBehavior<TestDbContext>();
                builder.WithBehavior(typeof(ApplicationBehavior<>));
                builder.WithInboxBehavior<TestDbContext>();
            });

            sequence.Should().Equal(_orderIndependentSequenceAroundApplicationBehavior);
        }

        [Fact]
        public void MustOrderBehaviorsAroundApplicationBehaviorIndependentlyOfRedundantUnitOfWorkRequests()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithOutboxProcessingBehavior<TestDbContext>();
                builder.WithBehavior(typeof(ApplicationBehavior<>));
                builder.WithInboxBehavior<TestDbContext>();
                builder.WithUnitOfWorkBehavior<TestDbContext>();
            });

            sequence.Should().Equal(_orderIndependentSequenceAroundApplicationBehavior);
        }

        [Fact]
        public void MustOrderBehaviorsAroundApplicationBehaviorIndependentlyOfRepeatedInboxRequests()
        {
            var sequence = ResolveBehaviorSequence(builder =>
            {
                builder.WithInboxBehavior<TestDbContext>();
                builder.WithBehavior(typeof(ApplicationBehavior<>));
                builder.WithOutboxProcessingBehavior<TestDbContext>();
                builder.WithInboxBehavior<TestDbContext>();
            });

            sequence.Should().Equal(_orderIndependentSequenceAroundApplicationBehavior);
        }

        // A partial configuration must keep the wrapping invariant over the behaviors it actually asked for,
        // and must not invent the one it did not.
        [Fact]
        public void MustOrderBehaviorsForInboxOnlyConfiguration()
        {
            var sequence = ResolveBehaviorSequence(builder => builder.WithInboxBehavior<TestDbContext>());

            sequence.Should().Equal(
                typeof(UnitOfWorkBehavior<TestCommand>),
                typeof(InboxBehavior<TestCommand>));
        }

        [Fact]
        public void MustOrderBehaviorsForOutboxOnlyConfiguration()
        {
            var sequence = ResolveBehaviorSequence(builder => builder.WithOutboxProcessingBehavior<TestDbContext>());

            sequence.Should().Equal(
                typeof(OutboxProcessingBehavior<TestCommand>),
                typeof(UnitOfWorkBehavior<TestCommand>));
        }

        private sealed class TestCommand : ICommand
        {
        }

        // Stands in for a behavior the application registers for itself. It is registered as an open generic
        // through the same public seam the reliability extensions use, so it lands as an ordinary foreign
        // ICommandBehavior<> descriptor rather than one the reliability extensions own.
        private sealed class ApplicationBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
                => next();
        }

        private sealed class TestDbContext : DbContext
        {
            public TestDbContext(DbContextOptions options) : base(options) { }
        }
    }
}
