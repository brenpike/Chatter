using System;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Provides details from a brokered message required for use with a message broker.
    /// </summary>
    public interface IBrokeredMessageDetailProvider
    {
        public string GetMessageName<T>();
        public string GetMessageName(Type type);
        public string GetReceiverName<T>();
        public string GetNextMessageName<T>();
        public string GetCompensatingMessageName<T>();
        public string GetBrokeredMessageDescription<T>();
        public bool AutoReceiveMessages<T>();
    }
}
