namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptions
    {
        public bool RouteMessagesToOutbox { get; set; } = false;
        public double MinutesToLiveInMemory { get; set; } = 10;
        public bool EnableOutboxPollingProcessor { get; set; } = false;
        public int OutboxProcessingIntervalInMilliseconds { get; set; } = 3000;
    }
}
