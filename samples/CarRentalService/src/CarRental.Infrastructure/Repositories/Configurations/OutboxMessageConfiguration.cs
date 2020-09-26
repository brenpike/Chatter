using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Repositories.Configurations
{
    public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
    {
        public void Configure(EntityTypeBuilder<OutboxMessage> builder)
        {
            builder.HasKey(t => t.MessageId);
            builder.Property(t => t.MessageId).IsRequired();
            builder.Property(t => t.ProcessedFromOutboxAtUtc);
            builder.Property(t => t.SentToOutboxAtUtc).IsRequired();
            builder.Property(t => t.MessageBody).IsRequired();
            builder.Property(t => t.MessageContext).IsRequired();
            builder.Property(t => t.MessageContentType).IsRequired();
            builder.Property(t => t.Destination).IsRequired();
            builder.Property(t => t.BatchId).IsRequired();
        }
    }
}
