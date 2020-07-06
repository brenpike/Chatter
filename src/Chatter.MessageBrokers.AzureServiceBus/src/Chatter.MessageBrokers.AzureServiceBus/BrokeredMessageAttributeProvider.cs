using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Provides details from the <see cref="BrokeredMessageAttribute"/> of a brokered message required for use with a message broker.
    /// </summary>
    public class BrokeredMessageAttributeProvider : IBrokeredMessageDetailProvider
    {
        public string GetBrokeredMessageDescription<T>()
        {
            var operationDescription = typeof(T).TryGetBrokeredMessageAttribute().MessageDescription;
            return string.IsNullOrWhiteSpace(operationDescription) ? GetReceiverName<T>() : operationDescription;
        }

        public string GetCompensatingMessageName<T>()
        {
            return typeof(T).TryGetBrokeredMessageAttribute()?.CompensatingMessage;
        }

        /// <summary>
        /// Get the <see cref="BrokeredMessageAttribute.MessageName"/> from a class decorated with a <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="T">A class decorated with a <see cref="BrokeredMessageAttribute"/></typeparam>
        /// <returns><see cref="BrokeredMessageAttribute.MessageName"/></returns>
        public string GetMessageName<T>()
        {
            return GetMessageName(typeof(T));
        }

        public string GetNextMessageName<T>()
        {
            return typeof(T).TryGetBrokeredMessageAttribute()?.NextMessage;
        }

        /// <summary>
        /// Get the <see cref="BrokeredMessageAttribute.ReceiverName"/> from a class decorated with a <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="T">A class decorated with a <see cref="BrokeredMessageAttribute"/></typeparam>
        /// <returns><see cref="BrokeredMessageAttribute.ReceiverName"/></returns>
        public string GetReceiverName<T>()
        {
            var messageName = GetMessageName<T>();
            var receiverName = typeof(T).TryGetBrokeredMessageAttribute()?.ReceiverName;

            if (receiverName == null)
            {
                return null;
            }

            if (messageName == receiverName)
            {
                return messageName;
            }

            return EntityNameHelper.FormatSubscriptionPath(messageName, receiverName);
        }

        public bool AutoReceiveMessages<T>()
        {
            return typeof(T).TryGetBrokeredMessageAttribute()?.AutoReceiveMessages ?? false;
        }

        public string GetMessageName(Type type)
        {
            var message = type.TryGetBrokeredMessageAttribute()?.MessageName;
            return message;
        }
    }
}
