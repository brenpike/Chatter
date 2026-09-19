using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingReliabilityRetentionPurgeService
{
    // Retention Purge statement granularity over a relational provider that needs no container, so the bound on a
    // purge DELETE is pinned on every host rather than only where Docker runs. The three facts in
    // Integration/WhenPurgingRetentionOnSqlServer own WHICH rows a pass deletes; these two own HOW MANY statements
    // it takes to delete them.
    //
    // ELIMINATED CLASS: a retention DELETE that touches an unbounded number of rows. Both facts seed one row more
    // than a chunk holds and count DELETE statements, because both the chunked and the unbounded shape delete the
    // same rows - a row-count assertion cannot separate them, and only the statement count can.
    public class WhenPurgingRetentionOverSqlite
    {
        // Mirrors ReliabilityRetentionPurgeService.RetentionPurgeChunkSize, which is private. One row more than a
        // chunk is the smallest backlog needing two statements, and it is also what makes the expected count exact:
        // the second chunk comes back SHORT, so a loop ending on a short chunk issues two statements while one
        // ending on an empty chunk issues three.
        private const int ChunkSize = 1000;
        private const int BacklogSize = ChunkSize + 1;

        private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(1);

        [Fact]
        public async Task MustPurgeAnOutboxBacklogInChunkedStatements()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var stale = DateTime.UtcNow - RetentionWindow - TimeSpan.FromHours(1);

            using (var seed = harness.CreateContext())
            {
                for (var index = 0; index < BacklogSize; index++)
                {
                    seed.Set<OutboxMessage>().Add(CreateOutboxMessage($"outbox-stale-{index}", stale, stale));
                }

                await seed.SaveChangesAsync();
            }

            var options = new EntityFrameworkReliabilityOptions { ProcessedOutboxRetention = RetentionWindow };
            var deleteStatements = await PurgeOnceAsync(harness, options);

            deleteStatements.Should().Be(2,
                "a backlog one row larger than a chunk is deleted by one full chunk and one short one, never by a single unbounded DELETE");

            using var verify = harness.CreateContext();
            (await verify.Set<OutboxMessage>().CountAsync()).Should().Be(0,
                "chunking bounds each statement, it does not leave rows past their retention behind");
        }

        [Fact]
        public async Task MustPurgeAnInboxBacklogInChunkedStatements()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var stale = DateTime.UtcNow - RetentionWindow - TimeSpan.FromHours(1);

            using (var seed = harness.CreateContext())
            {
                for (var index = 0; index < BacklogSize; index++)
                {
                    seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = $"inbox-stale-{index}", ReceivedByInboxAtUtc = stale });
                }

                await seed.SaveChangesAsync();
            }

            var options = new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = RetentionWindow };
            var deleteStatements = await PurgeOnceAsync(harness, options);

            deleteStatements.Should().Be(2,
                "the inbox is chunked through the same helper as the outbox, so its first purge of an accumulated table is bounded too");

            using var verify = harness.CreateContext();
            (await verify.Set<InboxMessage>().CountAsync()).Should().Be(0,
                "chunking bounds each statement, it does not leave markers past the Deduplication Window behind");
        }

        // A chunk is a row-limiting operator, and EF raises RowLimitingOperationWithoutOrderByWarning for one that
        // is not ordered. A TContext whose owner has turned that warning into a throw would then fault on every
        // chunk, be swallowed by the purge loop's per-pass handler, and reclaim nothing while logging an error
        // every PurgeInterval - a purge that has stopped working and says so only at Error level. This fact holds
        // the ordering in place: widen the helper's parameter to IQueryable and drop both OrderBy calls, and it
        // reddens with exactly that InvalidOperationException while the two statement-counting facts above stay
        // green, because only a context configured this way can see the difference.
        [Fact]
        public async Task MustPurgeThroughAContextThatRefusesUnorderedRowLimiting()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var stale = DateTime.UtcNow - RetentionWindow - TimeSpan.FromHours(1);

            using (var seed = harness.CreateContext())
            {
                seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = "inbox-stale", ReceivedByInboxAtUtc = stale });
                seed.Set<OutboxMessage>().Add(CreateOutboxMessage("outbox-stale", stale, stale));
                await seed.SaveChangesAsync();
            }

            var options = new EntityFrameworkReliabilityOptions
            {
                InboxDeduplicationWindow = RetentionWindow,
                ProcessedOutboxRetention = RetentionWindow
            };

            await PurgeOnceAsync(harness, options,
                builder => builder.ConfigureWarnings(warnings => warnings.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning)));

            using var verify = harness.CreateContext();
            (await verify.Set<InboxMessage>().CountAsync()).Should().Be(0,
                "a chunk the purge orders raises no row-limiting warning for the context to throw on");
            (await verify.Set<OutboxMessage>().CountAsync()).Should().Be(0,
                "a chunk the purge orders raises no row-limiting warning for the context to throw on");
        }

        // The purge service takes an IServiceScopeFactory rather than a context: it outlives any one scope, so it
        // opens a fresh one per cycle. A real container supplies the factory here so the seam is driven exactly as
        // the hosted service drives it, and the scoped context carries the counting interceptor.
        private static async Task<int> PurgeOnceAsync(SqliteOutboxContextHarness harness,
                                                      EntityFrameworkReliabilityOptions options,
                                                      Action<DbContextOptionsBuilder<SqliteOutboxContext>> configureContext = null)
        {
            var interceptor = new DeleteCountingInterceptor();

            var services = new ServiceCollection();
            services.AddScoped(_ => harness.CreateContext(builder => ConfigureContext(builder, interceptor, configureContext)));

            await using var provider = services.BuildServiceProvider();
            var purgeService = new ReliabilityRetentionPurgeService<SqliteOutboxContext>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                options,
                NullLogger<ReliabilityRetentionPurgeService<SqliteOutboxContext>>.Instance);

            await purgeService.PurgeOnceAsync(CancellationToken.None);

            return interceptor.DeleteStatementCount;
        }

        private static void ConfigureContext(DbContextOptionsBuilder<SqliteOutboxContext> builder,
                                             DeleteCountingInterceptor interceptor,
                                             Action<DbContextOptionsBuilder<SqliteOutboxContext>> configureContext)
        {
            builder.AddInterceptors(interceptor);
            configureContext?.Invoke(builder);
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

        /// <summary>
        /// Counts the DELETE statements a purge pass issues.
        /// </summary>
        /// <remarks>
        /// INVARIANT: only the async non-query path is counted, and only commands whose text starts with DELETE.
        /// ExecuteDeleteAsync dispatches one non-query command per call, and the seeding context is built without
        /// this interceptor, so nothing but the pass's own deletes is ever seen.
        /// </remarks>
        private sealed class DeleteCountingInterceptor : DbCommandInterceptor
        {
            private readonly List<string> _deleteStatements = new List<string>();

            public int DeleteStatementCount => _deleteStatements.Count;

            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                                                                                      CommandEventData eventData,
                                                                                      InterceptionResult<int> result,
                                                                                      CancellationToken cancellationToken = default)
            {
                if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    _deleteStatements.Add(command.CommandText);
                }

                return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            }
        }
    }
}
