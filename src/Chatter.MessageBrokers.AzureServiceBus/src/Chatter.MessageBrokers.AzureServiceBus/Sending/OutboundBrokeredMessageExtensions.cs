using Chatter.MessageBrokers.AzureServiceBus.Core;
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
                CorrelationId = brokeredMessage.GetCorrelationId(),
                ContentType = brokeredMessage.GetContentType(),
                Label = brokeredMessage.GetSubject(),
                ReplyTo = brokeredMessage.GetReplyToAddress(),
                ReplyToSessionId = brokeredMessage.GetReplyToGroupId(),
                SessionId = brokeredMessage.GetGroupId(),
                PartitionKey = brokeredMessage.GetPartitionKey(),
                ViaPartitionKey = brokeredMessage.GetViaPartitionKey(),
                To = brokeredMessage.GetToAddress()
            }
            .WithHashedBodyMessageId(brokeredMessage.MessageId)
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
            outboundBrokeredMessage.MessageContext[ASBHeaders.ScheduledEnqueueTimeUtc] = scheduledEnqueueTimeUtc;
            return outboundBrokeredMessage;
        }

        public static DateTime? GetScheduledEnqueueTimeUtc(this OutboundBrokeredMessage outboundBrokeredMessage)
        {
            return (DateTime?)outboundBrokeredMessage.GetMessageContextByKey(ASBHeaders.ScheduledEnqueueTimeUtc);
        }

        public static OutboundBrokeredMessage WithTo(this OutboundBrokeredMessage outboundBrokeredMessage, string to)
        {
            outboundBrokeredMessage.MessageContext[ASBHeaders.To] = to;
            return outboundBrokeredMessage;
        }

        public static string GetToAddress(this OutboundBrokeredMessage outboundBrokeredMessage)
        {
            return (string)outboundBrokeredMessage.GetMessageContextByKey(ASBHeaders.To);
        }

        public static OutboundBrokeredMessage WithViaPartitionKey(this OutboundBrokeredMessage outboundBrokeredMessage, string viaPartitionKey)
        {
            outboundBrokeredMessage.MessageContext[ASBHeaders.ViaPartitionKey] = viaPartitionKey;
            return outboundBrokeredMessage;
        }

        public static string GetViaPartitionKey(this OutboundBrokeredMessage outboundBrokeredMessage)
        {
            return (string)outboundBrokeredMessage.GetMessageContextByKey(ASBHeaders.ViaPartitionKey);
        }

        public static OutboundBrokeredMessage WithPartitionKey(this OutboundBrokeredMessage outboundBrokeredMessage, string partitionKey)
        {
            outboundBrokeredMessage.MessageContext[ASBHeaders.PartitionKey] = partitionKey;
            return outboundBrokeredMessage;
        }

        public static string GetPartitionKey(this OutboundBrokeredMessage outboundBrokeredMessage)
        {
            return (string)outboundBrokeredMessage.GetMessageContextByKey(ASBHeaders.PartitionKey);
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
