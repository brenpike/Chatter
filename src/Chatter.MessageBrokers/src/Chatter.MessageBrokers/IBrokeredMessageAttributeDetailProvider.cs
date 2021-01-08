using System;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Provides details from a brokered message required for use with a message broker.
    /// </summary>
    public interface IBrokeredMessageAttributeDetailProvider
    {
        public string GetMessageName<T>();
        public string GetMessageName(Type type);
        public string GetReceiverName<T>();
        public string GetErrorQueueName<T>();
        public string GetBrokeredMessageDescription<T>();
        public string GetInfrastructureType<T>();
    }
}
