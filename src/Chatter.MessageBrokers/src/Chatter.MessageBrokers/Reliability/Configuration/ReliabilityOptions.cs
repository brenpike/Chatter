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
    }
}
