using Chatter.MessageBrokers.Reliability.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// A relational DbContext backed by a real SQL Server database. Unlike <c>SqliteOutboxContext</c>,
    /// it applies the PRODUCTION <see cref="OutboxMessageConfiguration"/> and <see cref="InboxMessageConfiguration"/>
    /// rather than a hand-rolled model.
    ///
    /// INVARIANT: applying the production configurations is LOAD-BEARING. They give the outbox table the Id
    /// primary key plus the <c>ProcessedFromOutboxAtUtc</c> concurrency token, and key the inbox on MessageId.
    /// SqliteOutboxContext deliberately drops the concurrency token and keys the outbox on MessageId, which would
    /// make the optimistic-concurrency claim test prove nothing — so it must NOT be reused for the SQL Server suite.
    /// </summary>
    public sealed class SqlServerOutboxContext : DbContext
    {
        public SqlServerOutboxContext(DbContextOptions<SqlServerOutboxContext> options)
            : base(options)
        { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
            modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        }
    }

    /// <summary>
    /// Wraps a SQL Server connection string and hands out fresh <see cref="SqlServerOutboxContext"/> instances over
    /// it. A one-time <see cref="DatabaseFacade.EnsureCreated"/> initialises the schema. Two <see cref="CreateContext"/>
    /// calls over the same connection string give the "two contexts, same DB/row" setup the optimistic-concurrency
    /// claim test needs.
    /// </summary>
    public sealed class SqlServerOutboxContextHarness
    {
        private readonly string _connectionString;

        private SqlServerOutboxContextHarness(string connectionString)
            => _connectionString = connectionString;

        public static SqlServerOutboxContextHarness Create(string connectionString)
        {
            var harness = new SqlServerOutboxContextHarness(connectionString);
            using (var context = harness.CreateContext())
            {
                // INVARIANT: EnsureCreated (NOT Migrate) — the module ships no migrations.
                context.Database.EnsureCreated();
            }

            return harness;
        }

        public SqlServerOutboxContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<SqlServerOutboxContext>()
                .UseSqlServer(_connectionString)
                .Options;

            return new SqlServerOutboxContext(options);
        }
    }
}
