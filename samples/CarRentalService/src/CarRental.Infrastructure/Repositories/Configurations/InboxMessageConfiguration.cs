using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Repositories.Configurations
{
    public class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
    {
        public void Configure(EntityTypeBuilder<InboxMessage> builder)
        {
            builder.HasKey(t => t.MessageId);
            builder.Property(t => t.MessageId).IsRequired();
            builder.Property(t => t.ReceivedByInboxAtUtc);
        }
    }
}
