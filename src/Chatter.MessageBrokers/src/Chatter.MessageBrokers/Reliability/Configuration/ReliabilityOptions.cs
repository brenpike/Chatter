using Chatter.MessageBrokers.Reliability.Outbox;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptions
    {
        public bool OutboxEnabled { get; set; } = false;
        public double TimeToLiveInMinutes { get; set; } = 10;
        public int OutboxIntervalInMilliseconds { get; set; } = 3000;
    }
}
