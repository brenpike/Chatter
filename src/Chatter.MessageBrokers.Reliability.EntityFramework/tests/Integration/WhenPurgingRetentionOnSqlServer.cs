using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Integration
{
    // Retention purge over a real SQL Server database with the PRODUCTION model. ExecuteDeleteAsync compiles to a
    // single DELETE statement that no in-memory provider translates, so the only place the predicates can be proven
    // is against a relational database.
    //
    // ELIMINATED CLASS: an UNPROCESSED outbox row is purged. The outbox predicate requires a processed stamp, so a
    // row the drain has not claimed is outside the delete's reach rather than merely younger than the cutoff - the
    // unprocessed-old row seeded below is older than every other row and still survives.
    [Trait("Category", "Integration")]
    [Collection(EfReliabilitySqlServerCollection.Name)]
    public class WhenPurgingRetentionOnSqlServer
    {
        private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(1);

        private readonly EfReliabilitySqlServerFixture _fixture;

        public WhenPurgingRetentionOnSqlServer(EfReliabilitySqlServerFixture fixture)
            => _fixture = fixture;

        [RequiresDockerFact]
        public async Task MustPurgeOnlyTheRowsPastTheirRetentionWindow()
        {
            var harness = await CreateHarnessAsync();
            var now = DateTime.UtcNow;
            var stale = now - RetentionWindow - TimeSpan.FromHours(1);
            var fresh = now - TimeSpan.FromMinutes(1);

            using (var seed = harness.CreateContext())
            {
                seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = "inbox-stale", ReceivedByInboxAtUtc = stale });
                seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = "inbox-fresh", ReceivedByInboxAtUtc = fresh });
                seed.Set<OutboxMessage>().Add(CreateOutboxMessage("outbox-processed-stale", stale, stale));
                seed.Set<OutboxMessage>().Add(CreateOutboxMessage("outbox-processed-fresh", stale, fresh));
                seed.Set<OutboxMessage>().Add(CreateOutboxMessage("outbox-unprocessed-stale", stale, null));
                await seed.SaveChangesAsync();
            }

            var options = new EntityFrameworkReliabilityOptions
            {
                InboxDeduplicationWindow = RetentionWindow,
                ProcessedOutboxRetention = RetentionWindow
            };

            await PurgeOnceAsync(harness, options);

            using var verify = harness.CreateContext();
            var remainingInbox = await verify.Set<InboxMessage>().Select(m => m.MessageId).ToListAsync();
            var remainingOutbox = await verify.Set<OutboxMessage>().Select(m => m.MessageId).ToListAsync();

            remainingInbox.Should().BeEquivalentTo(new[] { "inbox-fresh" },
                "only an inbox marker older than the Deduplication Window is purged");
            remainingOutbox.Should().BeEquivalentTo(new[] { "outbox-processed-fresh", "outbox-unprocessed-stale" },
                "an outbox row is purged only when it carries a processed stamp older than the retention window");
        }

        [RequiresDockerFact]
        public async Task MustPurgeNothingWhileBothRetentionWindowsAreDisabled()
        {
            var harness = await CreateHarnessAsync();
            var stale = DateTime.UtcNow - TimeSpan.FromDays(365);

            using (var seed = harness.CreateContext())
            {
                seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = "inbox-ancient", ReceivedByInboxAtUtc = stale });
                seed.Set<OutboxMessage>().Add(CreateOutboxMessage("outbox-ancient", stale, stale));
                await seed.SaveChangesAsync();
            }

            await PurgeOnceAsync(harness, new EntityFrameworkReliabilityOptions());

            using var verify = harness.CreateContext();
            (await verify.Set<InboxMessage>().CountAsync()).Should().Be(1, "a null window is disabled, not zero");
            (await verify.Set<OutboxMessage>().CountAsync()).Should().Be(1, "a null retention is disabled, not zero");
        }

        // A marker whose ReceivedByInboxAtUtc is NULL is a delivery the inbox claimed and whose handler never
        // completed, not a marker of unknown age: the column carries two states, and the row's own presence is the
        // claim. Such a row becomes durable whenever a caller swallows the handler failure and commits anyway, which
        // is deliberate - the next delivery reads it as unhandled and runs the handler again - but no cutoff on a
        // NULL column ever ages it out, so a configured Deduplication Window would accrue those rows for the life of
        // the table if the predicate spared them. The rationale is in
        // docs/adr/0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md.
        //
        // The observation is the row's PRESENCE, not its column values: the purge issues a DELETE rather than an
        // update, so an assertion on what the row holds could not tell a spared row from a reclaimed one.
        [RequiresDockerFact]
        public async Task MustPurgeAnInboxMarkerClaimedButNeverHandled()
        {
            var harness = await CreateHarnessAsync();

            using (var seed = harness.CreateContext())
            {
                seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = "inbox-unstamped", ReceivedByInboxAtUtc = null });
                await seed.SaveChangesAsync();
            }

            var options = new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = TimeSpan.FromTicks(1) };

            await PurgeOnceAsync(harness, options);

            using var verify = harness.CreateContext();
            (await verify.Set<InboxMessage>().CountAsync()).Should().Be(0,
                "a marker left unstamped is a claim no handler completed, and no cutoff would ever age it out");
        }

        // The purge service takes an IServiceScopeFactory rather than a context: it outlives any one scope, so it
        // opens a fresh one per cycle. A real container supplies the factory here so the seam is driven exactly as
        // the hosted service drives it.
        private static async Task PurgeOnceAsync(SqlServerOutboxContextHarness harness, EntityFrameworkReliabilityOptions options)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => harness.CreateContext());

            await using var provider = services.BuildServiceProvider();
            var purgeService = new ReliabilityRetentionPurgeService<SqlServerOutboxContext>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                options,
                NullLogger<ReliabilityRetentionPurgeService<SqlServerOutboxContext>>.Instance);

            await purgeService.PurgeOnceAsync(CancellationToken.None);
        }

        private async Task<SqlServerOutboxContextHarness> CreateHarnessAsync()
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_retention_purge");
            return SqlServerOutboxContextHarness.Create(connectionString);
        }

        private static OutboxMessage CreateOutboxMessage(string messageId, DateTime sentAtUtc, DateTime? processedAtUtc)
            => new OutboxMessage
            {
                MessageId = messageId,
                Destination = "test-destination",
                MessageContext = "{}",
                MessageBody = "{}",
                MessageContentType = "application/json",
                SentToOutboxAtUtc = sentAtUtc,
                ProcessedFromOutboxAtUtc = processedAtUtc,
                BatchId = Guid.NewGuid()
            };
    }
}
