using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    /// <summary>
    /// Deletes inbox markers and processed outbox rows that have outlived the retention configured by
    /// <see cref="EntityFrameworkReliabilityOptions"/>.
    /// </summary>
    internal sealed class ReliabilityRetentionPurgeService<TContext> : BackgroundService where TContext : DbContext
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly EntityFrameworkReliabilityOptions _options;
        private readonly ILogger<ReliabilityRetentionPurgeService<TContext>> _logger;

        private const int RetentionPurgeChunkSize = 1000;

        public ReliabilityRetentionPurgeService(IServiceScopeFactory serviceScopeFactory,
                                                EntityFrameworkReliabilityOptions options,
                                                ILogger<ReliabilityRetentionPurgeService<TContext>> logger)
        {
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private bool IsRetentionConfigured
            => _options.InboxDeduplicationWindow.HasValue || _options.ProcessedOutboxRetention.HasValue;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!IsRetentionConfigured)
            {
                _logger.LogInformation($"Reliability retention is disabled for {typeof(TContext).Name}; no inbox or outbox rows will be purged.");
                return;
            }

            // INVARIANT: the purge must suspend before its first pass. BackgroundService.StartAsync runs this method
            // inline until it genuinely suspends, and every await below can complete synchronously against a database
            // that answers from cache, so without THIS yield the first purge of a large table would run on the host's
            // startup path. Task.Yield is what hands control back. The loop's Task.Delay cannot do it: that delay is
            // reached only AFTER a pass has already run, so it paces how often a pass repeats and never decides
            // whether the first one lands on the startup path.
            await Task.Yield();

            _logger.LogInformation($"Reliability retention purge is starting for {typeof(TContext).Name}.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception purgeFailure)
                {
                    // INVARIANT: one failed pass never ends the loop and never faults the host. A TContext whose model
                    // maps neither entity - retention is opt-in and the entity configurations are applied by the
                    // consumer - throws on every pass, and that must degrade to a logged error rather than take the
                    // application down at startup.
                    _logger.LogError(purgeFailure, $"Reliability retention purge failed for {typeof(TContext).Name}; the next pass will retry.");
                }

                try
                {
                    await Task.Delay(_options.PurgeInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation($"Reliability retention purge is stopping for {typeof(TContext).Name}.");
        }

        /// <summary>
        /// Runs one purge pass in a fresh scope.
        /// </summary>
        /// <remarks>
        /// INVARIANT: each cutoff is computed into a local BEFORE the predicate is written, so the expression tree
        /// closes over a captured instant rather than over a DateTime.UtcNow the provider would have to translate.
        /// Both predicates also demand a non-null timestamp: an inbox marker with no received time has no age to
        /// compare, and an outbox row with no processed stamp has not been drained yet, so neither is purgeable at
        /// all rather than merely younger than its cutoff. A relational provider already excludes those rows through
        /// three-valued logic, so the conjuncts state the rule rather than carry it; what carries it is the column
        /// each predicate keys on, pinned by
        /// Integration/WhenPurgingRetentionOnSqlServer.MustPurgeOnlyTheRowsPastTheirRetentionWindow, which goes red
        /// the moment the outbox predicate keys on SentToOutboxAtUtc instead.
        ///
        /// INVARIANT: a pass issues exactly ONE bounded DELETE per configured table and then returns. ELIMINATED
        /// CLASS: a purge whose per-pass work, or whose termination, depends on anything outside the purge's own
        /// configuration. This method holds no loop, so no writer, clock or replenishment rate can change how much
        /// one pass deletes or whether it ends; the only loop in this service is the one in ExecuteAsync, owned by
        /// stoppingToken and paced by PurgeInterval. An unbounded DELETE over the inbox or the outbox escalates to
        /// a lock on the very table the durability path writes through, and a per-pass loop bounding each statement
        /// only moves the unbounded quantity from the rows one statement touches to the statements one pass issues.
        /// TRADE, disclosed: a pass reclaims at most RetentionPurgeChunkSize rows per table - 1000 per table per
        /// PurgeInterval, 288,000 per table per day at the five-minute default - and PurgeInterval is the operator's
        /// dial on that rate. A table accruing eligible rows faster than that now never catches up, where a
        /// per-pass loop would have kept deleting; that is the deliberate cost, because the loop's exit was a claim
        /// about how fast writers replenish rather than a property of this service. Pinned by
        /// UsingReliabilityRetentionPurgeService/WhenPurgingRetentionOverSqlite.MustPurgeAnOutboxBacklogOneBoundedStatementPerPass
        /// and .MustPurgeAnInboxBacklogOneBoundedStatementPerPass, which seed one row more than a chunk, drive two
        /// passes, and count DELETE statements rather than deleted rows - a looping pass and a bounded one both end
        /// with the same rows gone, so only the statement count and what survives the FIRST pass separate them.
        /// Wrapping either call in a loop over DeleteOneChunkAsync reddens both at two statements on the first pass
        /// (observed - that is the shape they were written against); dropping the Take from the helper reddens both
        /// at a first pass that leaves nothing for the second (observed).
        /// </remarks>
        internal async Task PurgeOnceAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();

            if (_options.InboxDeduplicationWindow.HasValue)
            {
                var inboxCutoffUtc = DateTime.UtcNow - _options.InboxDeduplicationWindow.Value;
                var purgedMarkers = await DeleteOneChunkAsync(
                        context.Set<InboxMessage>()
                            .Where(marker => marker.ReceivedByInboxAtUtc != null && marker.ReceivedByInboxAtUtc < inboxCutoffUtc)
                            .OrderBy(marker => marker.ReceivedByInboxAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug($"Purged {purgedMarkers} inbox markers received before '{inboxCutoffUtc:O}'.");
            }

            if (_options.ProcessedOutboxRetention.HasValue)
            {
                var outboxCutoffUtc = DateTime.UtcNow - _options.ProcessedOutboxRetention.Value;
                var purgedMessages = await DeleteOneChunkAsync(
                        context.Set<OutboxMessage>()
                            .Where(message => message.ProcessedFromOutboxAtUtc != null && message.ProcessedFromOutboxAtUtc < outboxCutoffUtc)
                            .OrderBy(message => message.ProcessedFromOutboxAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug($"Purged {purgedMessages} outbox messages processed before '{outboxCutoffUtc:O}'.");
            }
        }

        /// <summary>
        /// Deletes the oldest <see cref="RetentionPurgeChunkSize"/> rows the query selects in ONE statement, and
        /// answers how many were deleted. Rows beyond that chunk are left for the next pass.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the query is ORDERED before it is taken from, which is why the parameter is an
        /// IOrderedQueryable rather than an IQueryable. A row-limiting operator with no ordering raises
        /// CoreEventId.RowLimitingOperationWithoutOrderByWarning, so a TContext registered with
        /// ConfigureWarnings(w => w.Throw(...)) would throw on every pass, be caught by the per-pass handler in
        /// ExecuteAsync, and purge nothing at all while logging an error every PurgeInterval. Pinned by
        /// UsingReliabilityRetentionPurgeService/WhenPurgingRetentionOverSqlite.MustPurgeThroughAContextThatRefusesUnorderedRowLimiting,
        /// which reddens with that InvalidOperationException when both OrderBy calls come off and this parameter is
        /// widened to IQueryable (observed). Ordering by the retention stamp also deletes the oldest rows first,
        /// which is the order a purge falling behind must make progress in for the remainder it leaves to be the
        /// NEWEST rows rather than an arbitrary slice.
        /// </remarks>
        private static Task<int> DeleteOneChunkAsync<TEntity>(IOrderedQueryable<TEntity> eligibleOldestFirst,
                                                              CancellationToken cancellationToken)
            where TEntity : class
            => eligibleOldestFirst.Take(RetentionPurgeChunkSize).ExecuteDeleteAsync(cancellationToken);
    }
}
