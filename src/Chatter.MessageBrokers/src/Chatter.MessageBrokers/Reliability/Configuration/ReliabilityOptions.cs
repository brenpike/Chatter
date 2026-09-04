namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptions
    {
        public bool RouteMessagesToOutbox { get; internal set; }
        public double MinutesToLiveInMemory { get; internal set; }
        public bool EnableOutboxPollingProcessor { get; internal set; }
        public int OutboxProcessingIntervalInMilliseconds { get; internal set; }
    }
}
