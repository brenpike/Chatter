namespace Chatter.MessageBrokers.AzureServiceBus.Core
{
    public static class ASBHeaders
    {
        public static readonly string ScheduledEnqueueTimeUtc = $"{ApplicationProperties.ChatterBaseHeader}.ScheduledEnqueueTimeUtc";
        public static readonly string To = $"{ApplicationProperties.ChatterBaseHeader}.To";
        public static readonly string ViaPartitionKey = $"{ApplicationProperties.ChatterBaseHeader}.ViaPartitionKey";
        public static readonly string PartitionKey = $"{ApplicationProperties.ChatterBaseHeader}.PartitionKey";
    }
}
