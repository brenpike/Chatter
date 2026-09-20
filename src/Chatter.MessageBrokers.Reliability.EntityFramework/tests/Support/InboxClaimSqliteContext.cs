using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// A relational DbContext carrying the inbox alone, built by applying the PRODUCTION
    /// <see cref="InboxMessageConfiguration"/> rather than a hand-written copy of it, so a claim runs against the
    /// same MessageId primary key the package ships.
    ///
    /// INVARIANT: the claim path needs a provider that supplies a real ambient transaction, and the EF Core
    /// InMemory provider supplies none - its Database.CurrentTransaction is null and its BeginTransactionAsync
    /// raises TransactionIgnoredWarning as an error - so every fact that reaches a claim runs here instead.
    /// </summary>
    public sealed class InboxClaimSqliteContext : DbContext
    {
        public InboxClaimSqliteContext(DbContextOptions<InboxClaimSqliteContext> options)
            : base(options)
        { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    }

    /// <summary>
    /// Owns a single open <see cref="SqliteConnection"/> to "DataSource=:memory:" for its lifetime. The in-memory
    /// SQLite database is destroyed when the last connection to it closes, so the connection is held open until the
    /// harness is disposed. Every <see cref="CreateContext"/> call returns a fresh context over that same
    /// connection, so a second context can be enlisted in a first context's open transaction and read what that
    /// transaction has flushed but not committed.
    /// </summary>
    public sealed class InboxClaimSqliteHarness : IDisposable, IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private InboxClaimSqliteHarness(SqliteConnection connection)
            => _connection = connection;

        public static InboxClaimSqliteHarness Create()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var harness = new InboxClaimSqliteHarness(connection);
            using (var context = harness.CreateContext())
            {
                context.Database.EnsureCreated();
            }

            return harness;
        }

        public InboxClaimSqliteContext CreateContext()
            => CreateContext(_ => { });

        /// <summary>
        /// Returns a context over the same connection whose options the caller may extend - adding an interceptor,
        /// for instance - before they are built.
        /// </summary>
        public InboxClaimSqliteContext CreateContext(Action<DbContextOptionsBuilder<InboxClaimSqliteContext>> configureOptions)
        {
            var optionsBuilder = new DbContextOptionsBuilder<InboxClaimSqliteContext>()
                .UseSqlite(_connection);

            configureOptions(optionsBuilder);

            return new InboxClaimSqliteContext(optionsBuilder.Options);
        }

        public void Dispose()
            => _connection.Dispose();

        public async ValueTask DisposeAsync()
            => await _connection.DisposeAsync();
    }
}
