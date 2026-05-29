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

        private sealed class ConfiguredContext : DbContext
        {
            public ConfiguredContext(DbContextOptions options) : base(options) { }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
                => modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        }
    }
}
