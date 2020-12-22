using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Provides details from the <see cref="BrokeredMessageAttribute"/> of a brokered message required for use with a message broker.
    /// </summary>
    class BrokeredMessageAttributeProvider : IBrokeredMessageAttributeDetailProvider
    {
        public string GetBrokeredMessageDescription<T>()
        {
            var operationDescription = typeof(T).TryGetBrokeredMessageAttribute().MessageDescription;
            return string.IsNullOrWhiteSpace(operationDescription) ? GetReceiverName<T>() : operationDescription;
        }

        /// <summary>
        /// Get the <see cref="BrokeredMessageAttribute.SendingPath"/> from a class decorated with a <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="T">A class decorated with a <see cref="BrokeredMessageAttribute"/></typeparam>
        /// <returns><see cref="BrokeredMessageAttribute.SendingPath"/></returns>
        public string GetMessageName<T>()
            => GetMessageName(typeof(T));

        /// <summary>
        /// Get the <see cref="BrokeredMessageAttribute.ReceiverName"/> from a class decorated with a <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="T">A class decorated with a <see cref="BrokeredMessageAttribute"/></typeparam>
        /// <returns><see cref="BrokeredMessageAttribute.ReceiverName"/></returns>
        public string GetReceiverName<T>()
            => typeof(T).TryGetBrokeredMessageAttribute()?.ReceiverName;

        public string GetMessageName(Type type)
            => type.TryGetBrokeredMessageAttribute()?.SendingPath;

        public string GetErrorQueueName<T>()
            => typeof(T).TryGetBrokeredMessageAttribute()?.ErrorQueueName;

        public string GetInfrastructureType<T>()
            => typeof(T).TryGetBrokeredMessageAttribute()?.InfrastructureType;
    }
}
