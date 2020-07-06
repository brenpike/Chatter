using Chatter.MessageBrokers.Options;

namespace Chatter.MessageBrokers.AzureServiceBus.Core
{
    public static class ASBHeaders
    {
        public static readonly string ScheduledEnqueueTimeUtc = $"{Headers.ChatterBaseHeader}.ScheduledEnqueueTimeUtc";
        public static readonly string To = $"{Headers.ChatterBaseHeader}.To";
        public static readonly string ViaPartitionKey = $"{Headers.ChatterBaseHeader}.ViaPartitionKey";
        public static readonly string PartitionKey = $"{Headers.ChatterBaseHeader}.PartitionKey";
    }
}
