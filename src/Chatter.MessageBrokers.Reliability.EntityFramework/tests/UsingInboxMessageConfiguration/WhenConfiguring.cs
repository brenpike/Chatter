using Chatter.MessageBrokers.Reliability.Inbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingInboxMessageConfiguration
{
    public class WhenConfiguring : Testing.Core.Context
    {
        private readonly DbContext _context;

        public WhenConfiguring()
        {
            var options = new DbContextOptionsBuilder<ConfiguredContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _context = new ConfiguredContext(options);
        }

        private Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType
            => _context.Model.FindEntityType(typeof(InboxMessage));

        [Fact]
        public void MustUseMessageIdAsPrimaryKey()
        {
            var key = EntityType.FindPrimaryKey();

            key.Properties.Should().ContainSingle().Which.Name.Should().Be(nameof(InboxMessage.MessageId));
        }

        [Fact]
        public void MustRequireMessageId()
        {
            var property = EntityType.FindProperty(nameof(InboxMessage.MessageId));

            property.IsNullable.Should().BeFalse();
        }

        [Fact]
        public void MustExposeReceivedDate()
        {
            var property = EntityType.FindProperty(nameof(InboxMessage.ReceivedByInboxAtUtc));

            property.Should().NotBeNull();
        }

        [Fact]
        public void MustAllowReceivedDateToBeNullable()
        {
            var property = EntityType.FindProperty(nameof(InboxMessage.ReceivedByInboxAtUtc));

            property.IsNullable.Should().BeTrue();
        }

        // INVARIANT: ReceivedByInboxAtUtc is the inbox's sole concurrency token, which is what puts the column in
        // the WHERE clause of the claim's own UPDATE so two deliveries racing the same message id are separated by
        // the store rather than by a read. A token on MessageId would put the primary key in that predicate twice.
        [Fact]
        public void MustTreatReceivedDateAsTheOnlyConcurrencyToken()
        {
            var concurrencyTokens = EntityType.GetProperties().Where(p => p.IsConcurrencyToken);

            concurrencyTokens.Should().ContainSingle()
                .Which.Name.Should().Be(nameof(InboxMessage.ReceivedByInboxAtUtc));
        }

        private sealed class ConfiguredContext : DbContext
        {
            public ConfiguredContext(DbContextOptions options) : base(options) { }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
                => modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        }
    }
}
