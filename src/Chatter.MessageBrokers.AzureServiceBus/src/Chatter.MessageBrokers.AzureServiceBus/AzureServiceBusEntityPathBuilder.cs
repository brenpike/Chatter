namespace Chatter.MessageBrokers.AzureServiceBus
{
    class AzureServiceBusEntityPathBuilder : IBrokeredMessagePathBuilder
    {
        // INVARIANT: reproduces the path shape of Azure Service Bus' internal EntityNameFormatter
        // (FormatSubscriptionPath / FormatRulePath). The SDK exposes no public path formatter,
        // so the canonical segment literals are inlined.
        private const string SubscriptionsSegment = "Subscriptions";
        private const string RulesSegment = "Rules";

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

            return FormatSubscriptionPath(messageSendingPath, messageReceiverPath);
        }

        string IBrokeredMessagePathBuilder.GetMessageReceivingRulePath(string messageSendingPath, string messageReceiverPath, string ruleName)
            => $"{FormatSubscriptionPath(messageSendingPath, messageReceiverPath)}/{RulesSegment}/{ruleName}";

        private static string FormatSubscriptionPath(string topicPath, string subscriptionName)
            => $"{topicPath}/{SubscriptionsSegment}/{subscriptionName}";

        string IBrokeredMessagePathBuilder.GetMessageSendingPath(string messageSendingPath) 
            => messageSendingPath;
    }
}
