using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Provides details from the <see cref="BrokeredMessageAttribute"/> of a brokered message required for use with a message broker.
    /// </summary>
    class BrokeredMessageAttributeProvider : IBrokeredMessageAttributeDetailProvider
    {
        private static readonly ConcurrentDictionary<Type, BrokeredMessageAttribute> _brokeredMessageAttributesByType = new();

        // Collectible types bypass the process-lifetime cache: a strong Type key would keep their
        // AssemblyLoadContext from unloading.
        private static BrokeredMessageAttribute GetBrokeredMessageAttribute(Type type)
            => type is { IsCollectible: true }
                ? type.TryGetBrokeredMessageAttribute()
                : _brokeredMessageAttributesByType.GetOrAdd(type, static t => t.TryGetBrokeredMessageAttribute());

        public string GetBrokeredMessageDescription<T>()
        {
            var operationDescription = GetBrokeredMessageAttribute(typeof(T)).MessageDescription;
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
            => GetBrokeredMessageAttribute(typeof(T))?.ReceiverName;

        public string GetMessageName(Type type)
            => GetBrokeredMessageAttribute(type)?.SendingPath;

        public string GetErrorQueueName<T>()
            => GetBrokeredMessageAttribute(typeof(T))?.ErrorQueueName;

        public string GetInfrastructureType<T>()
            => GetBrokeredMessageAttribute(typeof(T))?.InfrastructureType;
    }
}
