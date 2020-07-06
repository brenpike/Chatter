using Chatter.MessageBrokers.Options;
using Microsoft.Azure.ServiceBus;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Chatter.MessageBrokers.AzureServiceBus.Extensions
{
    public static class MessageExtensions
    {
        public static Message WithHashedBodyMessageId(this Message message, string messageId)
        {
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                return message;
            }

            using var sha265Provider = new SHA256CryptoServiceProvider();
            var hash = sha265Provider.ComputeHash(message.Body);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hash)
                sb.Append(b.ToString("X2"));

            message.MessageId = sb.ToString();
            return message;
        }

        public static Message WithUserProperties(this Message message, IDictionary<string, object> userProperties)
        {
            foreach (var kvp in userProperties)
            {
                message.UserProperties[kvp.Key] = kvp.Value;
            }
            return message;
        }

        public static TransactionMode GetTransactionMode(this Message message)
        {
            if (message.UserProperties.ContainsKey(Headers.TransactionMode))
            {
                return (TransactionMode)message.UserProperties[Headers.TransactionMode];
            }
            else
            {
                return TransactionMode.None;
            }
        }

        public static Message AddUserProperty(this Message message, string name, object value)
        {
            message.UserProperties[name] = value;
            return message;
        }

        public static Message CacheInitialExpiryTimeUtc(this Message message)
        {
            if (!message.UserProperties.TryGetValue(Headers.ExpiryTimeUtc, out var expiryDateUtc))
            {
                message.UserProperties[Headers.ExpiryTimeUtc] = message.ExpiresAtUtc;
            }

            return message;
        }
    }
}
