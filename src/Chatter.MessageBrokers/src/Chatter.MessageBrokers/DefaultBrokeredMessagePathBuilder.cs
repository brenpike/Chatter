namespace Chatter.MessageBrokers
{
    class DefaultBrokeredMessagePathBuilder : IBrokeredMessagePathBuilder
    {
        string IBrokeredMessagePathBuilder.GetMessageReceivingPath(string messageSendingPath, string messageReceiverPath) 
            => messageReceiverPath;

        string IBrokeredMessagePathBuilder.GetMessageReceivingRulePath(string messageSendingPath, string messageReceiverPath, string ruleName) 
            => messageReceiverPath;

        string IBrokeredMessagePathBuilder.GetMessageSendingPath(string messageSendingPath) 
            => messageSendingPath;
    }
}
