namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptions
    {
        public bool OutboxEnabled { get; set; } = false;
        public int TimeToLiveInMinutes { get; set; } = 10;
        public int OutboxIntervalInMilliseconds { get; set; } = 3000;
    }
}
