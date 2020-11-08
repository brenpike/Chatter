namespace Chatter.MessageBrokers
{
    public interface IBrokeredMessagePathBuilder
    {
        public string GetMessageSendingPath(string messageSendingPath);
        public string GetMessageRecevingRulePath(string messageSendingPath, string messageReceiverPath, string ruleName);
        public string GetMessageReceivingPath(string messageSendingPath, string messageReceiverPath);
    }
}
