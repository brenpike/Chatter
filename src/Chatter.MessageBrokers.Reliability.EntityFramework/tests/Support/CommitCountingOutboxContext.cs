using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// A DbContext over the EF Core InMemory provider that counts every asynchronous save it is asked to perform.
    /// The model mirrors <see cref="SqliteOutboxContext"/> so the outbox adapter sees the production entity shape,
    /// while the provider keeps the harness free of any native SQLite or SQL Server dependency.
    /// </summary>
    public sealed class CommitCountingOutboxContext : DbContext
    {
        public CommitCountingOutboxContext(DbContextOptions<CommitCountingOutboxContext> options)
            : base(options)
        { }

        public static CommitCountingOutboxContext Create()
        {
            var options = new DbContextOptionsBuilder<CommitCountingOutboxContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new CommitCountingOutboxContext(options);
        }

        public int SaveChangesAsyncCallCount { get; private set; }

        // INVARIANT: each override forwards to the BASE two-argument implementation rather than to the sibling
        // virtual overload, so one logical save increments the counter exactly once. Forwarding the single-argument
        // overload to base.SaveChangesAsync(CancellationToken) would re-enter the two-argument override through
        // virtual dispatch and count the same save twice.
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesAsyncCallCount++;
            return base.SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            SaveChangesAsyncCallCount++;
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

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
                    b.Property(t => t.MessageContentType).IsRequired();
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
