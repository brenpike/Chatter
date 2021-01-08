using Chatter.MessageBrokers.AzureServiceBus;

namespace Chatter.MessageBrokers
{
    public static class InfrastructureTypesExtension
    {
        public static string AzureServiceBus(this InfrastructureTypes _) => ASBMessageContext.InfrastructureType;
    }
}
