using Azure.Messaging.ServiceBus;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Chatter.MessageBrokers.AzureServiceBus.Extensions
{
    public static class MessageExtensions
    {
        public static ServiceBusMessage WithHashedBodyMessageId(this ServiceBusMessage message, string messageId)
        {
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                message.MessageId = messageId;
                return message;
            }

            using var sha265Provider = new SHA256CryptoServiceProvider();
            var hash = sha265Provider.ComputeHash(message.Body.ToArray());
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hash)
                sb.Append(b.ToString("X2"));

            message.MessageId = sb.ToString();
            return message;
        }

        public static ServiceBusMessage WithApplicationProperties(this ServiceBusMessage message, IDictionary<string, object> applicationProperties)
        {
            foreach (var kvp in applicationProperties)
            {
                message.ApplicationProperties[kvp.Key] = kvp.Value;
            }
            return message;
        }

        public static ServiceBusMessage AddApplicationProperty(this ServiceBusMessage message, string name, object value)
        {
            message.ApplicationProperties[name] = value;
            return message;
        }
    }
}
