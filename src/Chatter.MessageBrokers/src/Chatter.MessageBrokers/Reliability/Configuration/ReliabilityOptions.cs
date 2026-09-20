using System;

namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptions
    {
        public bool RouteMessagesToOutbox { get; internal set; }
        public double MinutesToLiveInMemory { get; internal set; }
        public bool EnableOutboxPollingProcessor { get; internal set; }
        public int OutboxProcessingIntervalInMilliseconds { get; internal set; }

        /// <summary>
        /// The deduplication window of the in-memory inbox, in minutes: after a receipt completes, a redelivery of the
        /// same message id is skipped for this long. Default value is 60, and a value below 1 is refused while the
        /// options are being built. The same window is the lease on an in-flight reservation, so a handler that runs
        /// longer than the window is pre-empted by a second handler for the same message id and its own completion is
        /// then discarded - a handler that can outrun the window must be idempotent.
        /// </summary>
        public int InMemoryInboxDeduplicationWindowInMinutes { get; internal set; }

        /// <summary>
        /// The most receipts the in-memory inbox will retain. Default value is 200000. This is a memory safety valve
        /// rather than the deduplication guarantee: under memory pressure it truncates the window advertised by
        /// <see cref="InMemoryInboxDeduplicationWindowInMinutes"/>, so a redelivery within that window can be handled
        /// again once the receipt has been evicted.
        /// </summary>
        public int InMemoryInboxMaxEntries { get; internal set; }

        /// <summary>
        /// The most outbox messages a single poll takes, so that poll cost is bounded by this number rather than by
        /// the number of unprocessed messages. Default value is 100, and a value below 1 is refused while the options
        /// are being built. Unlike its siblings, this property carries its default as an initializer rather than
        /// taking it from <see cref="ReliabilityOptionsBuilder"/>: a directly constructed
        /// <see cref="ReliabilityOptions"/> that never passed through that builder would otherwise name a batch of
        /// zero, which drains nothing at all.
        /// </summary>
        public int OutboxPollBatchSize { get; internal set; } = 100;

        /// <summary>
        /// The wait before a message that has failed dispatch once is attempted again, in seconds. Default value is
        /// 5, and a value below 1 is refused while the options are being built. The wait doubles per further attempt
        /// up to <see cref="OutboxDispatchBackoffCapInSeconds"/>.
        /// </summary>
        /// <remarks>
        /// INVARIANT: like <see cref="OutboxPollBatchSize"/>, this property and
        /// <see cref="OutboxDispatchBackoffCapInSeconds"/> carry their defaults as initializers rather than taking
        /// them from <see cref="ReliabilityOptionsBuilder"/>: the backoff is not opt-in, so a directly constructed
        /// <see cref="ReliabilityOptions"/> - which this repository's own tests build - must back off too, and with
        /// no initializer it would wait no time at all. Oracle:
        /// <c>WhenBuilding.MustBackOffFromADirectlyConstructedReliabilityOptions</c>; removing this initializer
        /// reddens it and nothing else (observed).
        /// </remarks>
        public int OutboxDispatchBackoffBaseInSeconds { get; internal set; } = 5;

        /// <summary>
        /// The longest wait the doubling reaches, in seconds. Default value is 60, and a value below 1 is refused
        /// while the options are being built. A cap below the base names a fixed wait of the cap rather than a
        /// growing one, which is accepted.
        /// </summary>
        public int OutboxDispatchBackoffCapInSeconds { get; internal set; } = 60;

        /// <summary>
        /// The most times dispatch of one outbox message may be attempted. Null - the default - disables the ceiling,
        /// which re-attempts a message for good; a value below 1 is refused while the options are being built.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the ceiling is the OPT-IN part of the durable attempt state and defaults to absent, because a
        /// finite default would start abandoning messages a host already running this package keeps re-attempting
        /// today. Oracle: <c>WhenBuilding.MustAcceptAnOmittedOutboxMaxDispatchAttempts</c>, read together with
        /// <c>WhenBuilding.MustBuildDefaultOptions</c>; seeding <c>ReliabilityOptionsBuilder</c>'s own field for this
        /// setting with a value reddens both and nothing else (observed). An initializer on THIS property would
        /// redden neither, because <c>Resolve</c> assigns every property from that builder field. This is the same
        /// reasoning
        /// <c>EntityFrameworkReliabilityOptions.ProcessedOutboxRetention</c> defaults to null under. The BACKOFF
        /// above is deliberately not opt-in: it is the mechanism that keeps a permanently-failing message from
        /// holding its place at the head of every poll batch, so it ships working.
        /// </remarks>
        public int? OutboxMaxDispatchAttempts { get; internal set; }

        /// <summary>
        /// The wait owed before a message that has already cost <paramref name="dispatchAttempts"/> failed attempts
        /// is attempted again.
        /// </summary>
        /// <param name="dispatchAttempts">The attempt count the message carries</param>
        /// <returns>The wait, starting at the base, doubling per attempt and stopping at the cap</returns>
        /// <remarks>
        /// INVARIANT: the wait is derived from the attempt count the message already carries, so the schedule needs
        /// no stored column beyond <see cref="Outbox.OutboxMessage.NextAttemptAtUtc"/> itself. Oracle:
        /// <c>WhenBuilding.MustDoubleTheDispatchBackoffPerAttemptAndStopAtTheCap</c>. Dropping the
        /// <see cref="Math.Max"/> against one reddens its 0 and <see cref="int.MinValue"/> cases and nothing else
        /// (observed). Dropping the <see cref="Math.Min"/> against the cap reddens its 5, 6 and
        /// <see cref="int.MaxValue"/> cases, plus <c>WhenBuilding.MustBackOffFromADirectlyConstructedReliabilityOptions</c>
        /// and <c>WhenBuilding.MustAcceptAnOutboxDispatchBackoffCapBelowItsBase</c> - five facts, every one of them an
        /// oracle for this same cap, and nothing else (observed).
        /// <para>
        /// The whole computation stays in double, the way
        /// <c>Recovery.Retry.ExponentialDelayRetry.GetDelayTimeInMillisecondsFromRetryAttempts</c> does: an attempt
        /// count large enough to send <see cref="Math.Pow"/> to infinity is then clamped to the cap by the same
        /// comparison every other count takes, rather than wrapping an exponent computed in <see cref="int"/>.
        /// </para>
        /// </remarks>
        internal TimeSpan CalculateDispatchBackoff(int dispatchAttempts)
        {
            var attemptsAlreadyMade = Math.Max(dispatchAttempts, 1);
            var delayInSeconds = Math.Min(OutboxDispatchBackoffBaseInSeconds * Math.Pow(2d, attemptsAlreadyMade - 1d),
                                          OutboxDispatchBackoffCapInSeconds);
            return TimeSpan.FromSeconds(delayInSeconds);
        }
    }
}
