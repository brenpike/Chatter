using Chatter.MessageBrokers.Reliability.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    internal sealed class BrokeredMessageOutboxProcessor : BackgroundService
    {
        /// <summary>
        /// The identity count at which a drain stops re-polling and takes the processing interval. The count is
        /// read AFTER a poll's identities are tallied, so the poll that crosses the ceiling is kept whole and one
        /// drain retains up to MaxDrainIdentities + OutboxPollBatchSize - 1 identities — 10,099 at the default
        /// Outbox Poll Batch of 100. At that batch the ceiling takes no FEWER than 100 consecutive polls, and more
        /// than that whenever polls overlap and each adds fewer than a full batch of unseen identities.
        /// Internal so the test pinning the ceiling reads the number rather than restating it.
        /// </summary>
        internal const int MaxDrainIdentities = 10000;

        private readonly ILogger<BrokeredMessageOutboxProcessor> _logger;
        private readonly ReliabilityOptions _reliabilityOptions;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public BrokeredMessageOutboxProcessor(ILogger<BrokeredMessageOutboxProcessor> logger,
                                              ReliabilityOptions reliabilityOptions,
                                              IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reliabilityOptions = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"BrokeredMessageOutboxProcessor is starting.");

            stoppingToken.Register(() =>
                _logger.LogDebug($" BrokeredMessageOutboxProcessor background task is stopping."));

            // INVARIANT: the poller must suspend before its first poll. Every await below can complete
            // synchronously - the in-memory Pollable Outbox Store answers from a dictionary - and the drain loop
            // below has no unconditional wait, so without this the whole drain would run inline on the caller's
            // thread and StartAsync would not return until the backlog was empty. Task.Delay alone used to
            // guarantee this hand-off once per poll; the drain loop no longer reaches it once per poll.
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogTrace($"BrokeredMessageOutboxProcessor is now processing messages...");

                await DrainOutboxAsync(stoppingToken);

                await Task.Delay(_reliabilityOptions.OutboxProcessingIntervalInMilliseconds, stoppingToken);
            }

            _logger.LogInformation($"BrokeredMessageOutboxProcessor background task is stopping.");
        }

        /// <summary>
        /// Polls while the Pollable Outbox Store keeps answering with a full Outbox Poll Batch of messages this
        /// drain has not already seen, so a backlog larger than the batch size leaves in one interval rather than
        /// one batch per interval.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the drain ends on a poll that adds no message identity it has not already seen.
        /// <see cref="OutboxProcessor.Process"/> logs and swallows every dispatch failure, so a batch of exactly the
        /// batch size that cannot be dispatched is re-fetched on the next poll; without this guard a full poison
        /// batch would spin against the store forever and never reach the interval wait below. The seen set spans
        /// the WHOLE drain and is keyed on identity rather than on position, so neither the order a poll returns
        /// rows in nor which side of the store's cap a row tied on
        /// <see cref="OutboxMessage.SentToOutboxAtUtc"/> falls on can read as progress. Pinned by
        /// MustStopRepollingWhenAFullOutboxPollBatchRepeatsInADifferentOrder and
        /// MustStopRepollingWhenOverlappingOutboxPollBatchesAddNoUnseenMessage in
        /// UsingBrokeredMessageOutboxProcessor.WhenSendingOutboxMessages, which go red the moment the comparison is
        /// narrowed to the batch immediately before — the first of them when that comparison is positional, the
        /// second however it is written, since its consecutive batches are never equal as sets either.
        /// <para>
        /// INVARIANT: the identity of a row is the (Id, MessageId) PAIR. InMemoryBrokeredMessageOutbox never assigns
        /// an Id, so every row it hands back carries Id 0 and an Id-only key would end every one of its drains on
        /// the second poll, capping the default outbox at one batch per interval. Pinned by
        /// MustStopRepollingWhenOverlappingOutboxPollBatchesAddNoUnseenMessage, whose rows all carry Id 0 and which
        /// goes red the moment MessageId is dropped from the key.
        /// </para>
        /// <para>
        /// INVARIANT: the drain also ends once it has seen <see cref="MaxDrainIdentities"/> identities, which is
        /// what bounds the seen set. That stop reports only that this drain has run long enough; it is NOT a claim
        /// that the store made no progress, and whatever is still unprocessed is taken by the next poll after the
        /// interval wait. The ceiling is read AFTER the poll's identities are tallied, so the poll that crosses it
        /// is kept whole and the set holds up to <see cref="MaxDrainIdentities"/> plus one Outbox Poll Batch less
        /// one. Pinned by MustEndTheDrainOnceTheDrainIdentityCeilingIsReached, which goes red the moment the
        /// ceiling leaves the loop condition, and by
        /// MustEndTheDrainOnTheFirstPollThatCrossesTheDrainIdentityCeiling, whose batch does not divide the ceiling
        /// evenly and which goes red the moment the loop stops SHORT of crossing it — reading the ceiling as
        /// seenIdentities.Count + OutboxPollBatchSize &lt;= MaxDrainIdentities leaves the first of the two green and
        /// only the second red.
        /// </para>
        /// </remarks>
        private async Task DrainOutboxAsync(CancellationToken stoppingToken)
        {
            var seenIdentities = new HashSet<(int Id, string MessageId)>();
            bool hasMoreToDrain;

            do
            {
                var batch = await SendOutboxMessagesAsync(stoppingToken);
                var unseenCount = batch.Count(message => seenIdentities.Add((message.Id, message.MessageId)));
                var storeAnsweredWithUnseenWork = batch.Count >= _reliabilityOptions.OutboxPollBatchSize && unseenCount > 0;
                var isWithinIdentityCeiling = seenIdentities.Count < MaxDrainIdentities;

                if (storeAnsweredWithUnseenWork && !isWithinIdentityCeiling)
                {
                    _logger.LogInformation($"Outbox drain has run long enough at {seenIdentities.Count} messages and is ending; the rest is taken on the next poll.");
                }

                hasMoreToDrain = storeAnsweredWithUnseenWork && isWithinIdentityCeiling;
            }
            while (hasMoreToDrain && !stoppingToken.IsCancellationRequested);
        }

        /// <summary>
        /// Takes ONE Outbox Poll Batch in a FRESH scope and returns the rows it fetched, or no rows when the poll or
        /// a dispatch faults, so the caller falls through to the interval wait rather than retrying immediately.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the scope is per POLL, not per drain. A drain of a large backlog would otherwise accumulate
        /// every row of every batch in one store's change tracker for the whole drain. What the drain does carry
        /// across polls is the identity set alone, and deliberately so: a store that stamps a row processed on the
        /// instance before it saves hands that row back unstamped from the next poll's fresh store when the save
        /// fails, so only a set spanning the whole drain keeps it from being dispatched again immediately. That the
        /// set outlives a poll is pinned by MustStopRepollingWhenOverlappingOutboxPollBatchesAddNoUnseenMessage in
        /// UsingBrokeredMessageOutboxProcessor.WhenSendingOutboxMessages, which goes red the moment the set is
        /// reset or pruned between polls. NO test pins the scope being per poll: MustCreateScopePerDrainPass
        /// asserts CreateScope with Times.AtLeastOnce against a drain of a single poll, so hoisting one scope to
        /// span a whole drain leaves it green.
        /// </remarks>
        private async Task<IReadOnlyList<OutboxMessage>> SendOutboxMessagesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var outbox = (IPollableOutboxStore)scope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>();
                var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
                var messages = (await outbox.GetUnprocessedMessagesFromOutbox(cancellationToken)).ToList();

                if (messages.Count == 0)
                {
                    _logger.LogTrace($"No messages available for processing in outbox.");
                    return messages;
                }

                _logger.LogTrace($"{messages.Count} messages available for processing in outbox.");

                foreach (var message in messages.OrderBy(m => m.SentToOutboxAtUtc))
                {
                    await processor.Process(message, cancellationToken);
                }

                return messages;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error sending outbox messages");
                return Array.Empty<OutboxMessage>();
            }
        }
    }
}
