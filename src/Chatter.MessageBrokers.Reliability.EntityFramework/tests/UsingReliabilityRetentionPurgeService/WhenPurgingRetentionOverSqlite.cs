using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingReliabilityRetentionPurgeService
{
    // Retention Purge behaviour over a relational provider that needs no container, so what is pinned here is
    // pinned on every host rather than only where Docker runs. Integration/WhenPurgingRetentionOnSqlServer owns
    // WHICH rows a pass deletes against the production model; the two backlog facts below own HOW MUCH one pass
    // does and what it leaves for the next one, and MustPurgeAnInboxMarkerClaimedButNeverHandledWhileSparingAFreshOne
    // carries the inbox predicate's two-state reading of ReceivedByInboxAtUtc onto hosts with no Docker.
    //
    // ELIMINATED CLASS: a purge whose per-pass work, or whose termination, depends on anything outside the purge's
    // own configuration. Both facts seed one row more than a chunk holds and then drive TWO passes, asserting one
    // DELETE statement per pass with the remainder taken by the next pass. They count statements rather than rows
    // because a pass that loops until the table is drained and a pass that issues one bounded statement both end
    // with the same rows gone - only the statement count, and what survives the FIRST pass, separate them.
    public class WhenPurgingRetentionOverSqlite : Testing.Core.Context
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

        // ReceivedByInboxAtUtc carries two states rather than one age: a marker present with a NULL stamp is a
        // delivery the inbox claimed and whose handler never completed, and a marker present with a stamp was
        // handled at that instant. A claimed-but-unhandled marker can reach the table durably - a caller that
        // swallows the handler failure and commits anyway leaves one, deliberately, so the next delivery reads it
        // as unhandled - and no cutoff on a NULL column would ever age it out, so the predicate takes it as
        // eligible outright. The rationale is in
        // docs/adr/0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md.
        //
        // The seed pairs the unstamped marker with a stamped one INSIDE the window so the assertion separates the
        // new eligibility from a predicate that simply deletes every marker, and the observation is which
        // MessageIds are still PRESENT: the purge deletes rather than updates, so an assertion on a surviving row's
        // column values could not tell a spared marker from a reclaimed one. The eligible set is far smaller than
        // one chunk, so nothing here depends on where a provider sorts NULLs.
        [Fact]
        public async Task MustPurgeAnInboxMarkerClaimedButNeverHandledWhileSparingAFreshOne()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var stale = DateTime.UtcNow - RetentionWindow - TimeSpan.FromHours(1);
            var fresh = DateTime.UtcNow - TimeSpan.FromMinutes(1);

            using (var seed = harness.CreateContext())
            {
                seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = "inbox-unstamped", ReceivedByInboxAtUtc = null });
                seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = "inbox-stale", ReceivedByInboxAtUtc = stale });
                seed.Set<InboxMessage>().Add(new InboxMessage { MessageId = "inbox-fresh", ReceivedByInboxAtUtc = fresh });
                await seed.SaveChangesAsync();
            }

            var options = new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = RetentionWindow };

            await PurgeOnceAsync(harness, options);

            using var verify = harness.CreateContext();
            var remainingMarkers = await verify.Set<InboxMessage>().Select(marker => marker.MessageId).ToListAsync();

            remainingMarkers.Should().BeEquivalentTo(new[] { "inbox-fresh" },
                "an unstamped marker is eligible outright and a stamped one only once it is older than the Deduplication Window");
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

        // A host stopping mid-pass cancels stoppingToken, and every await PurgeOnceAsync makes is observing it, so
        // the pass completes by throwing OperationCanceledException. That is the shutdown working, not a retention
        // incident: the loop condition is already false by the time the handler runs, so the "next pass will retry"
        // the broad handler promises never comes and an operator reads an Error-level failure on every clean stop.
        //
        // This fact drives the hosted service exactly as the host does - StartAsync, then StopAsync once the pass is
        // known to be in flight - and parks the pass's DELETE on the token the pass is observing, so the cancellation
        // arrives from inside the pass rather than from the interval wait, which already handles its own. It asserts
        // on Error-level logs rather than on an exception because the loop swallows either way; what separates the
        // two handlers is only what they record. Remove the cancellation-aware handler and this fact reddens on a
        // logged Error, and alone in this package, because only this one cancels a pass in flight.
        [Fact]
        public async Task MustTreatAPurgeCancelledByShutdownAsANormalStop()
        {
            using var harness = SqliteOutboxContextHarness.Create();
            var stale = DateTime.UtcNow - RetentionWindow - TimeSpan.FromHours(1);

            using (var seed = harness.CreateContext())
            {
                seed.Set<OutboxMessage>().Add(CreateOutboxMessage("outbox-stale", stale, stale));
                await seed.SaveChangesAsync();
            }

            var logger = New.Common().RecordingLogger<ReliabilityRetentionPurgeService<SqliteOutboxContext>>();
            var passIsInFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var interceptor = new DeleteParkingInterceptor(passIsInFlight);

            var services = new ServiceCollection();
            services.AddScoped(_ => harness.CreateContext(builder => builder.AddInterceptors(interceptor)));

            await using var provider = services.BuildServiceProvider();
            var purgeService = new ReliabilityRetentionPurgeService<SqliteOutboxContext>(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new EntityFrameworkReliabilityOptions { ProcessedOutboxRetention = RetentionWindow },
                logger.Creation);

            await purgeService.StartAsync(CancellationToken.None);
            await passIsInFlight.Task;
            await purgeService.StopAsync(CancellationToken.None);

            logger.CountOf(LogLevel.Error).Should().Be(0,
                "a pass the host cancelled on its way down is the shutdown working, not a purge that failed");
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
        /// Parks the pass's DELETE on the very token the pass is observing, so the pass is still in flight when the
        /// host stops and then completes by throwing <see cref="OperationCanceledException"/> exactly as a real
        /// ExecuteDeleteAsync cancelled mid-statement does.
        /// </summary>
        /// <remarks>
        /// INVARIANT: only DELETE is parked. The purge context is opened by the pass itself, so anything the
        /// provider issues on the way to the statement would otherwise park instead and the cancellation would
        /// arrive before the pass had begun its work.
        /// </remarks>
        private sealed class DeleteParkingInterceptor : DbCommandInterceptor
        {
            private readonly TaskCompletionSource<bool> _passIsInFlight;

            public DeleteParkingInterceptor(TaskCompletionSource<bool> passIsInFlight) => _passIsInFlight = passIsInFlight;

            public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                                                                                            CommandEventData eventData,
                                                                                            InterceptionResult<int> result,
                                                                                            CancellationToken cancellationToken = default)
            {
                if (!command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
                }

                _passIsInFlight.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return result;
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
