namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptions
    {
        public bool RouteMessagesToOutbox { get; set; }
        public double MinutesToLiveInMemory { get; set; }
        public bool EnableOutboxPollingProcessor { get; set; }
        public int OutboxProcessingIntervalInMilliseconds { get; set; }
    }
}
