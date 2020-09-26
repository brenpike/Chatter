using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Chatter.Testing.Core.Creators.MessageBrokers
{
    public class DbContextCreator : Creator<DbContext>
    {
        public DbContextCreator(INewContext newContext, DbContext creation = null)
            : base(newContext, creation)
        {
            var ob = new DbContextOptionsBuilder<FakeContext>().UseInMemoryDatabase("FakeDB").Options;
            Creation = new FakeContext(ob);
        }

        public DbContextCreator ThatHasOutboxMessage(OutboxMessage message)
        {
            Creation.Add(message);
            Creation.SaveChanges();

            return this;
        }

        private class FakeContext : DbContext
        {
            public FakeContext(DbContextOptions options) 
                : base(options)
            {}

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<OutboxMessage>(
                    b =>
                    {
                        b.HasKey(t => t.MessageId);
                        b.Property(t => t.MessageId).IsRequired();
                        b.Property(t => t.ProcessedFromOutboxAtUtc);
                        b.Property(t => t.SentToOutboxAtUtc).IsRequired();
                        b.Property(t => t.MessageBody).IsRequired();
                        b.Property(t => t.MessageContext).IsRequired();
                        b.Property(t => t.Destination).IsRequired();
                        b.Property(t => t.BatchId).IsRequired();
                    });

                modelBuilder.Entity<InboxMessage>(
                    b =>
                    {
                        b.HasKey(t => t.MessageId);
                        b.Property(t => t.MessageId).IsRequired();
                        b.Property(t => t.ReceivedByInboxAtUtc);
                    });
            }
        }
    }
}
