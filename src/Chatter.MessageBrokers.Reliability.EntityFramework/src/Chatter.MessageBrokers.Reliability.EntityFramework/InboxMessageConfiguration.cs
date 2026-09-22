using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    public class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
    {
        public void Configure(EntityTypeBuilder<InboxMessage> builder)
        {
            builder.HasKey(t => t.MessageId);
            builder.Property(t => t.MessageId).IsRequired();
            // INVARIANT: ReceivedByInboxAtUtc is the inbox's sole concurrency token. The column carries the claim's
            // own state, so putting it in the UPDATE's WHERE clause is what makes a delivery whose claim lost to a
            // concurrent one fail its write instead of overwriting the winner. This is an annotation on the model,
            // not a column: the token changes the SQL the provider emits and leaves the table shape alone.
            // Oracle: WhenConfiguring.MustTreatReceivedDateAsTheOnlyConcurrencyToken; removing this call reddens
            // that one fact and no other, measured on net8.0.
            // Rationale: docs/adr/0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md.
            builder.Property(t => t.ReceivedByInboxAtUtc).IsConcurrencyToken();
        }
    }
}
