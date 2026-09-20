using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    /// <summary>
    /// An opt-in Outbox Poll Index over <see cref="OutboxMessage.ProcessedFromOutboxAtUtc"/> then
    /// <see cref="OutboxMessage.SentToOutboxAtUtc"/>. The leading column is the one both unprocessed-message
    /// polls filter on; the second follows it because a drain works the oldest staged message first.
    /// </summary>
    /// <remarks>
    /// <see cref="OutboxMessageConfiguration"/> does not apply this index, so an application opts in by
    /// applying this configuration in its own <c>OnModelCreating</c>. Doing so is a model change like any
    /// other and the application owns the migration it generates for it. The index is plain -- neither
    /// filtered nor unique -- so it stays provider-neutral; a filtered index would need provider-specific SQL.
    /// </remarks>
    public class OutboxMessagePollIndexConfiguration : IEntityTypeConfiguration<OutboxMessage>
    {
        public void Configure(EntityTypeBuilder<OutboxMessage> builder)
        {
            builder.HasIndex(t => new { t.ProcessedFromOutboxAtUtc, t.SentToOutboxAtUtc });
        }
    }
}
