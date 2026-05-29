using Chatter.MessageBrokers.Reliability.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingOutboxMessageConfiguration
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
            => _context.Model.FindEntityType(typeof(OutboxMessage));

        [Fact]
        public void MustUseIdAsPrimaryKey()
        {
            var key = EntityType.FindPrimaryKey();

            key.Properties.Should().ContainSingle().Which.Name.Should().Be(nameof(OutboxMessage.Id));
        }

        [Fact]
        public void MustGenerateIdOnAdd()
        {
            var id = EntityType.FindProperty(nameof(OutboxMessage.Id));

            id.ValueGenerated.Should().Be(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd);
        }

        [Theory]
        [InlineData(nameof(OutboxMessage.MessageId))]
        [InlineData(nameof(OutboxMessage.SentToOutboxAtUtc))]
        [InlineData(nameof(OutboxMessage.MessageBody))]
        [InlineData(nameof(OutboxMessage.MessageContext))]
        [InlineData(nameof(OutboxMessage.MessageContentType))]
        [InlineData(nameof(OutboxMessage.Destination))]
        [InlineData(nameof(OutboxMessage.BatchId))]
        public void MustRequireProperty(string propertyName)
        {
            var property = EntityType.FindProperty(propertyName);

            property.IsNullable.Should().BeFalse();
        }

        [Fact]
        public void MustTreatProcessedDateAsConcurrencyToken()
        {
            var property = EntityType.FindProperty(nameof(OutboxMessage.ProcessedFromOutboxAtUtc));

            property.IsConcurrencyToken.Should().BeTrue();
        }

        [Fact]
        public void MustAllowProcessedDateToBeNullable()
        {
            var property = EntityType.FindProperty(nameof(OutboxMessage.ProcessedFromOutboxAtUtc));

            property.IsNullable.Should().BeTrue();
        }

        private sealed class ConfiguredContext : DbContext
        {
            public ConfiguredContext(DbContextOptions options) : base(options) { }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
                => modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        }
    }
}
