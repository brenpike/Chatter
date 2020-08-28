namespace Chatter.MessageBrokers.Routing.Options
{
    public class SendOptions : RoutingOptions
    {
        public SendOptions()
        {}

        public void SetReplyToAddress(string replyTo)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.ReplyToAddress, replyTo);
        }

        public void SetReplyToGroupId(string replyToGroupId)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.ReplyToGroupId, replyToGroupId);
        }
    }
}
