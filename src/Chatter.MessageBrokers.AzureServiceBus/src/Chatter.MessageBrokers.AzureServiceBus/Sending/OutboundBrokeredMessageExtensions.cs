using Chatter.MessageBrokers.AzureServiceBus.Extensions;
using Chatter.MessageBrokers.Sending;
using Microsoft.Azure.ServiceBus;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    public static class OutboundBrokeredMessageExtensions
    {
        public static Message AsAzureServiceBusMessage(this OutboundBrokeredMessage brokeredMessage)
        {
            var message = new Message(brokeredMessage.Body)
            {
                MessageId = string.IsNullOrWhiteSpace(brokeredMessage.MessageId) ? Guid.NewGuid().ToString() : brokeredMessage.MessageId,
                CorrelationId = brokeredMessage.CorrelationId,
                ContentType = brokeredMessage.ContentType,
                Label = brokeredMessage.Subject,
                ReplyTo = brokeredMessage.ReplyToAddress,
                ReplyToSessionId = brokeredMessage.ReplyToGroupId,
                SessionId = brokeredMessage.GroupId,
                PartitionKey = brokeredMessage.GetPartitionKey(),
                ViaPartitionKey = brokeredMessage.GetViaPartitionKey(),
                To = brokeredMessage.GetToAddress()
            }
            .WithUserProperties(brokeredMessage.MessageContext);

            if (brokeredMessage.GetTimeToLive() != null)
            {
                message.TimeToLive = brokeredMessage.GetTimeToLive().Value;
            }

            if (brokeredMessage.GetScheduledEnqueueTimeUtc() != null)
            {
                message.ScheduledEnqueueTimeUtc = brokeredMessage.GetScheduledEnqueueTimeUtc().Value;
            }

            return message;
        }

        public static OutboundBrokeredMessage WithScheduledEnqueueTimeUtc(this OutboundBrokeredMessage outboundBrokeredMessage, DateTime scheduledEnqueueTimeUtc)
        {
            outboundBrokeredMessage.MessageContext[ASBMessageContext.ScheduledEnqueueTimeUtc] = scheduledEnqueueTimeUtc;
            return outboundBrokeredMessage;
        }

        public static DateTime? GetScheduledEnqueueTimeUtc(this OutboundBrokeredMessage outboundBrokeredMessage)
        {
            return (DateTime?)outboundBrokeredMessage.GetMessageContextByKey(ASBMessageContext.ScheduledEnqueueTimeUtc);
        }

        public static OutboundBrokeredMessage WithTo(this OutboundBrokeredMessage outboundBrokeredMessage, string to)
        {
            outboundBrokeredMessage.MessageContext[ASBMessageContext.To] = to;
            return outboundBrokeredMessage;
        }

        public static string GetToAddress(this OutboundBrokeredMessage outboundBrokeredMessage)
        {
            return (string)outboundBrokeredMessage.GetMessageContextByKey(ASBMessageContext.To);
        }

        public static OutboundBrokeredMessage WithViaPartitionKey(this OutboundBrokeredMessage outboundBrokeredMessage, string viaPartitionKey)
        {
            outboundBrokeredMessage.MessageContext[ASBMessageContext.ViaPartitionKey] = viaPartitionKey;
            return outboundBrokeredMessage;
        }

        public static string GetViaPartitionKey(this OutboundBrokeredMessage outboundBrokeredMessage)
        {
            return (string)outboundBrokeredMessage.GetMessageContextByKey(ASBMessageContext.ViaPartitionKey);
        }

        public static OutboundBrokeredMessage WithPartitionKey(this OutboundBrokeredMessage outboundBrokeredMessage, string partitionKey)
        {
            outboundBrokeredMessage.MessageContext[ASBMessageContext.PartitionKey] = partitionKey;
            return outboundBrokeredMessage;
        }

        public static string GetPartitionKey(this OutboundBrokeredMessage outboundBrokeredMessage)
        {
            return (string)outboundBrokeredMessage.GetMessageContextByKey(ASBMessageContext.PartitionKey);
        }

        public static object GetApplicationPropertyByKey(this OutboundBrokeredMessage outboundBrokeredMessage, string key)
        {
            if (outboundBrokeredMessage.MessageContext.TryGetValue(key, out var output))
            {
                return output;
            }
            else
            {
                return null;
            }
        }
    }
}
