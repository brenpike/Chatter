using Microsoft.Azure.ServiceBus;

namespace Chatter.MessageBrokers.AzureServiceBus
{
    class AzureServiceBusEntityPathBuilder : IBrokeredMessagePathBuilder
    {
        string IBrokeredMessagePathBuilder.GetMessageReceivingPath(string messageSendingPath, string messageReceiverPath)
        {

            if (string.IsNullOrWhiteSpace(messageReceiverPath))
            {
                return null;
            }

            if (messageSendingPath == messageReceiverPath)
            {
                return messageSendingPath;
            }

            if (string.IsNullOrWhiteSpace(messageSendingPath) && !string.IsNullOrWhiteSpace(messageReceiverPath))
            {
                return messageReceiverPath;
            }

            return EntityNameHelper.FormatSubscriptionPath(messageSendingPath, messageReceiverPath);
        }

        string IBrokeredMessagePathBuilder.GetMessageRecevingRulePath(string messageSendingPath, string messageReceiverPath, string ruleName) 
            => EntityNameHelper.FormatRulePath(messageSendingPath, messageReceiverPath, ruleName);

        string IBrokeredMessagePathBuilder.GetMessageSendingPath(string messageSendingPath) 
            => messageSendingPath;
    }
}
