using Chatter.MessageBrokers.SqlServiceBroker;

namespace Chatter.MessageBrokers
{
    public static class InfrastructureTypesExtension
    {
        public static string SqlServiceBroker(this InfrastructureTypes _) => SSBMessageContext.InfrastructureType;
    }
}
