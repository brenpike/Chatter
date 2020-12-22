namespace Chatter.MessageBrokers
{
    public interface IBrokeredMessagePathBuilder
    {
        public string GetMessageSendingPath(string messageSendingPath);
        public string GetMessageReceivingRulePath(string messageSendingPath, string messageReceiverPath, string ruleName);
        public string GetMessageReceivingPath(string messageSendingPath, string messageReceiverPath);
    }
}
