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
    // purge pass is pinned on every host rather than only where Docker runs. The three facts in
    // Integration/WhenPurgingRetentionOnSqlServer own WHICH rows a pass deletes; these two own HOW MUCH one pass
    // does, and what it leaves for the next one.
    //
    // ELIMINATED CLASS: a purge whose per-pass work, or whose termination, depends on anything outside the purge's
    // own configuration. Both facts seed one row more than a chunk holds and then drive TWO passes, asserting one
    // DELETE statement per pass with the remainder taken by the next pass. They count statements rather than rows
    // because a pass that loops until the table is drained and a pass that issues one bounded statement both end
    // with the same rows gone - only the statement count, and what survives the FIRST pass, separate them.
    public class WhenPurgingRetentionOverSqlite
    {
        // Mirrors ReliabilityRetentionPurgeService.RetentionPurgeChunkSize, which is private. One row more than a
        // chunk is the smallest backlog that outlives a single pass, which is what makes the surviving count exact:
        // a pass bounded by the chunk leaves exactly one row, while a pass that loops leaves none.
        private const int ChunkSize = 1000;
        private const int BacklogSize = ChunkSize + 1;

        private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(1);

        [Fact]
        public async Task MustPurgeAnOutboxBacklogOneBoundedStatementPerPass()
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

            (await PurgeOnceAsync(harness, options)).Should().Be(1,
                "a pass issues one bounded DELETE per configured table and then returns, whatever the backlog behind it");

            using (var afterFirstPass = harness.CreateContext())
            {
                (await afterFirstPass.Set<OutboxMessage>().CountAsync()).Should().Be(BacklogSize - ChunkSize,
                    "the pass is bounded by the chunk rather than by the eligible set, so the overflow is left for the next pass");
            }

            (await PurgeOnceAsync(harness, options)).Should().Be(1,
                "the next pass is bounded the same way, however few rows are left for it");

            using var verify = harness.CreateContext();
            (await verify.Set<OutboxMessage>().CountAsync()).Should().Be(0,
                "successive bounded passes still reclaim every row past its retention");
        }

        [Fact]
        public async Task MustPurgeAnInboxBacklogOneBoundedStatementPerPass()
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

            (await PurgeOnceAsync(harness, options)).Should().Be(1,
                "the inbox is deleted through the same bounded helper as the outbox, so its pass is bounded too");

            using (var afterFirstPass = harness.CreateContext())
            {
                (await afterFirstPass.Set<InboxMessage>().CountAsync()).Should().Be(BacklogSize - ChunkSize,
                    "the pass is bounded by the chunk rather than by the eligible set, so the overflow is left for the next pass");
            }

            (await PurgeOnceAsync(harness, options)).Should().Be(1,
                "the next pass is bounded the same way, however few markers are left for it");

            using var verify = harness.CreateContext();
            (await verify.Set<InboxMessage>().CountAsync()).Should().Be(0,
                "successive bounded passes still reclaim every marker past the Deduplication Window");
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

        // A pass opens a DI scope and resolves TContext out of it, so whatever that resolution pulls is held by the
        // scope until it is released. A scoped member implementing ONLY IAsyncDisposable refuses a synchronous
        // release with InvalidOperationException, which aborts the pass on the way out - after its deletes have
        // already run - and leaves the loop in ExecuteAsync logging a failed pass every PurgeInterval for a purge
        // that is in fact working.
        //
        // This fact asserts the pass does not throw rather than that no error is logged, because it drives
        // PurgeOnceAsync directly: the refusal surfaces at the await here. Routed through ExecuteAsync the per-pass
        // handler would swallow it, and an assertion on the log would then be reporting that handler rather than
        // the release shape. Revert the scope to a synchronous release and only this fact reddens, because only
        // this one puts an async-only disposable in the scope.
        [Fact]
        public async Task MustReleaseThePurgeScopeAsynchronously()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var stale = DateTime.UtcNow - RetentionWindow - TimeSpan.FromHours(1);

            using (var seed = harness.CreateContext())
            {
                seed.Set<OutboxMessage>().Add(CreateOutboxMessage("outbox-stale", stale, stale));
                await seed.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new AsyncOnlyDisposableScopedService());
            services.AddScoped(scopedProvider => ResolveContextAfterAnAsyncOnlyDisposable(scopedProvider, harness));

            await using var provider = services.BuildServiceProvider();
            var purgeService = new ReliabilityRetentionPurgeService<SqliteOutboxContext>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new EntityFrameworkReliabilityOptions { ProcessedOutboxRetention = RetentionWindow },
                NullLogger<ReliabilityRetentionPurgeService<SqliteOutboxContext>>.Instance);

            await FluentActions.Invoking(() => purgeService.PurgeOnceAsync(CancellationToken.None))
                .Should().NotThrowAsync<InvalidOperationException>();

            using var verify = harness.CreateContext();
            (await verify.Set<OutboxMessage>().CountAsync()).Should().Be(0,
                "the pass still reclaims what it should through a graph whose release is asynchronous");
        }

        // The context the pass resolves pulls an AsyncOnlyDisposableScopedService out of the same scope, so the
        // scope holds a member a synchronous release refuses.
        private static SqliteOutboxContext ResolveContextAfterAnAsyncOnlyDisposable(IServiceProvider scopedProvider,
                                                                                    SqliteOutboxContextHarness harness)
        {
            scopedProvider.GetRequiredService<AsyncOnlyDisposableScopedService>();
            return harness.CreateContext();
        }

        // The purge service takes an IServiceScopeFactory rather than a context: it outlives any one scope, so it
        // opens a fresh one per cycle. A real container supplies the factory here so the seam is driven exactly as
        // the hosted service drives it, and the scoped context carries the counting interceptor.
        //
        // Each call builds its own interceptor, so the count it answers with belongs to THAT pass alone and two
        // successive calls report two independent per-pass counts over the one database the harness holds open.
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

        /// <summary>
        /// A service implementing ONLY <see cref="IAsyncDisposable"/>. A scope holding one throws
        /// <see cref="InvalidOperationException"/> when released synchronously, which is what lets a fact tell an
        /// asynchronous release from a synchronous one. Registered through a factory because the container's
        /// constructor discovery does not reach a private nested type.
        /// </summary>
        private sealed class AsyncOnlyDisposableScopedService : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => default;
        }
    }
}
