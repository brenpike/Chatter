using Chatter.MessageBrokers.Reliability.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingOutboxMessagePollIndexConfiguration
{
    public class WhenConfiguring : Testing.Core.Context
    {
        private readonly DbContext _pollIndexedContext;
        private readonly DbContext _shippedContext;

        public WhenConfiguring()
        {
            _pollIndexedContext = new PollIndexedContext(BuildOptions<PollIndexedContext>());
            _shippedContext = new ShippedContext(BuildOptions<ShippedContext>());
        }

        private static DbContextOptions<TContext> BuildOptions<TContext>() where TContext : DbContext
            => new DbContextOptionsBuilder<TContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

        private Microsoft.EntityFrameworkCore.Metadata.IEntityType PollIndexedEntityType
            => _pollIndexedContext.Model.FindEntityType(typeof(OutboxMessage));

        private Microsoft.EntityFrameworkCore.Metadata.IEntityType ShippedEntityType
            => _shippedContext.Model.FindEntityType(typeof(OutboxMessage));

        [Fact]
        public void MustIndexProcessedDateThenSentDate()
        {
            var index = PollIndexedEntityType.GetIndexes().Should().ContainSingle().Subject;

            index.Properties.Select(property => property.Name).Should().Equal(
                nameof(OutboxMessage.ProcessedFromOutboxAtUtc),
                nameof(OutboxMessage.SentToOutboxAtUtc));
        }

        [Fact]
        public void MustNotFilterThePollIndex()
        {
            var index = PollIndexedEntityType.GetIndexes().Single();

            index.GetFilter().Should().BeNull();
        }

        [Fact]
        public void MustNotMakeThePollIndexUnique()
        {
            var index = PollIndexedEntityType.GetIndexes().Single();

            index.IsUnique.Should().BeFalse();
        }

        [Fact]
        public void MustLeaveTheShippedConfigurationWithoutAnIndex()
        {
            ShippedEntityType.GetIndexes().Should().BeEmpty();
        }

        private sealed class PollIndexedContext : DbContext
        {
            public PollIndexedContext(DbContextOptions options) : base(options) { }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
                modelBuilder.ApplyConfiguration(new OutboxMessagePollIndexConfiguration());
            }
        }

        private sealed class ShippedContext : DbContext
        {
            public ShippedContext(DbContextOptions options) : base(options) { }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
                => modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        }
    }
}
