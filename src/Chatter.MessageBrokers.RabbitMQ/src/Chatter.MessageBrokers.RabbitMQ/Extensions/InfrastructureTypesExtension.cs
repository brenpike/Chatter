using Chatter.MessageBrokers.RabbitMQ;

namespace Chatter.MessageBrokers
{
    public static class InfrastructureTypesExtension
    {
        public static string RabbitMq(this InfrastructureTypes _) => RabbitMqMessageContext.InfrastructureType;
    }
}
