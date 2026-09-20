using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
    {
        public void Configure(EntityTypeBuilder<OutboxMessage> builder)
        {
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Id).IsRequired().ValueGeneratedOnAdd();
            builder.Property(t => t.MessageId).IsRequired();
            builder.Property(t => t.ProcessedFromOutboxAtUtc).IsConcurrencyToken();
            builder.Property(t => t.SentToOutboxAtUtc).IsRequired();
            builder.Property(t => t.MessageBody).IsRequired();
            builder.Property(t => t.MessageContext).IsRequired();
            builder.Property(t => t.MessageContentType).IsRequired();
            builder.Property(t => t.Destination).IsRequired();
            builder.Property(t => t.BatchId).IsRequired();
            // INVARIANT: ProcessedFromOutboxAtUtc stays the sole concurrency token. The failure path writes attempt
            // state outside the tracker, so a token on either column below would make that write fight the claim.
            // Oracle: WhenConfiguring.MustTreatProcessedDateAsTheOnlyConcurrencyToken; adding IsConcurrencyToken() to
            // either property reddens it and nothing else.
            builder.Property(t => t.DispatchAttempts).IsRequired().HasDefaultValue(0);
            builder.Property(t => t.NextAttemptAtUtc).IsRequired(false);
        }
    }
}
