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
        /// same message id is skipped for this long. Default value is 60. A non-positive value disables time-based
        /// expiry, so a completed receipt is remembered until <see cref="InMemoryInboxMaxEntries"/> evicts it.
        /// </summary>
        public int InMemoryInboxDeduplicationWindowInMinutes { get; internal set; }

        /// <summary>
        /// The most receipts the in-memory inbox will retain. Default value is 200000. This is a memory safety valve
        /// rather than the deduplication guarantee: under memory pressure it truncates the window advertised by
        /// <see cref="InMemoryInboxDeduplicationWindowInMinutes"/>, so a redelivery within that window can be handled
        /// again once the receipt has been evicted.
        /// </summary>
        public int InMemoryInboxMaxEntries { get; internal set; }
    }
}
