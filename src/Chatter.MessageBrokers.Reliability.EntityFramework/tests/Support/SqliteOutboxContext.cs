using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// A relational DbContext backed by a private in-memory SQLite database. Mirrors the model shape of
    /// Testing.Core's FakeContext so the reliability adapter's real transaction behavior (BeginTransaction,
    /// Commit, Rollback) can be exercised against a relational provider rather than the InMemory provider.
    /// </summary>
    public sealed class SqliteOutboxContext : DbContext
    {
        public SqliteOutboxContext(DbContextOptions<SqliteOutboxContext> options)
            : base(options)
        { }

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

    /// <summary>
    /// Owns a single open <see cref="SqliteConnection"/> to "DataSource=:memory:" for its lifetime. The in-memory
    /// SQLite database is destroyed when the last connection to it closes, so the connection is held open until the
    /// harness is disposed. Each <see cref="CreateContext"/> call returns a fresh context over the same connection,
    /// enabling commit/rollback assertions against reloaded state.
    /// </summary>
    public sealed class SqliteOutboxContextHarness : IDisposable, IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SqliteOutboxContextHarness(SqliteConnection connection)
            => _connection = connection;

        public SqliteConnection Connection => _connection;

        public static SqliteOutboxContextHarness Create()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var harness = new SqliteOutboxContextHarness(connection);
            using (var context = harness.CreateContext())
            {
                context.Database.EnsureCreated();
            }

            return harness;
        }

        public SqliteOutboxContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<SqliteOutboxContext>()
                .UseSqlite(_connection)
                .Options;

            return new SqliteOutboxContext(options);
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }
}
