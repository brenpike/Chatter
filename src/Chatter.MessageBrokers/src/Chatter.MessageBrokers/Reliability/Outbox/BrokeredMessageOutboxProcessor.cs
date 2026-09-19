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
        /// Polls until the Pollable Outbox Store answers with less than a full Outbox Poll Batch, so a backlog
        /// larger than the batch size leaves in one interval rather than one batch per interval.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the drain stops when a batch repeats unchanged. <see cref="OutboxProcessor.Process"/> logs and
        /// swallows every dispatch failure, so a batch of exactly the batch size that cannot be dispatched is
        /// re-fetched identically on the next poll; without this guard a full poison batch would spin against the
        /// store forever and never reach the interval wait below. A batch that made no progress therefore costs one
        /// wasted poll and then waits the interval like any other.
        /// </remarks>
        private async Task DrainOutboxAsync(CancellationToken stoppingToken)
        {
            IReadOnlyList<OutboxMessage> previousBatch = Array.Empty<OutboxMessage>();
            bool hasMoreToDrain;

            do
            {
                var batch = await SendOutboxMessagesAsync(stoppingToken);
                hasMoreToDrain = batch.Count >= _reliabilityOptions.OutboxPollBatchSize && !IsSameBatch(previousBatch, batch);
                previousBatch = batch;
            }
            while (hasMoreToDrain && !stoppingToken.IsCancellationRequested);
        }

        private static bool IsSameBatch(IReadOnlyList<OutboxMessage> previousBatch, IReadOnlyList<OutboxMessage> currentBatch)
        {
            if (previousBatch.Count != currentBatch.Count)
            {
                return false;
            }

            for (var index = 0; index < currentBatch.Count; index++)
            {
                // INVARIANT: the identity of a row is the (Id, MessageId) PAIR. InMemoryBrokeredMessageOutbox never
                // assigns an Id, so every row it hands back carries Id 0 and an Id-only comparison would report every
                // in-memory batch as a repeat and cap the default outbox at one poll per interval.
                if (previousBatch[index].Id != currentBatch[index].Id
                    || !string.Equals(previousBatch[index].MessageId, currentBatch[index].MessageId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Takes ONE Outbox Poll Batch in a FRESH scope and returns the rows it fetched, or no rows when the poll or
        /// a dispatch faults, so the caller falls through to the interval wait rather than retrying immediately.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the scope is per POLL, not per drain. A drain of a large backlog would otherwise accumulate
        /// every row of every batch in one store's change tracker for the whole drain.
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
