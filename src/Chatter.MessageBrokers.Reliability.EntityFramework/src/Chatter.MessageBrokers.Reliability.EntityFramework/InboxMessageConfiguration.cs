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
            // INVARIANT: ReceivedByInboxAtUtc is a concurrency token, which is what gives the expired-marker branch
            // of the inbox claim a loser to absorb. That branch refreshes the marker IN PLACE, so two deliveries of
            // one expired message id both UPDATE the same row and the MessageId primary key separates nothing; the
            // token carries the value each delivery read into its own UPDATE predicate, so the delivery that reaches
            // the row second matches no row. Oracles:
            // WhenConfiguring.MustTreatReceivedDateAsAConcurrencyToken and
            // WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenASecondDeliveryRefreshesTheSameExpiredMessageId;
            // removing IsConcurrencyToken() below reddens both and nothing else. See
            // docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md.
            builder.Property(t => t.ReceivedByInboxAtUtc).IsConcurrencyToken();
        }
    }
}
