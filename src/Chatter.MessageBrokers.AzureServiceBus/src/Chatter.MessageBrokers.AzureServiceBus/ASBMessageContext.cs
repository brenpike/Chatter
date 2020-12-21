namespace Chatter.MessageBrokers.AzureServiceBus
{
    public static class ASBMessageContext
    {
        public static readonly string InfrastructureType = $"{MessageContext.ChatterBaseHeader}.AzureServiceBus";

        public static readonly string ScheduledEnqueueTimeUtc = $"{MessageContext.ChatterBaseHeader}.ScheduledEnqueueTimeUtc";
        public static readonly string To = $"{MessageContext.ChatterBaseHeader}.To";
        public static readonly string ViaPartitionKey = $"{MessageContext.ChatterBaseHeader}.ViaPartitionKey";
        public static readonly string PartitionKey = $"{MessageContext.ChatterBaseHeader}.PartitionKey";
    }
}
