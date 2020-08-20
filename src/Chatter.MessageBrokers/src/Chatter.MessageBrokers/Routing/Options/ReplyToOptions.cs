namespace Chatter.MessageBrokers.Routing.Options
{
    public class ReplyToOptions : RoutingOptions
    {
        public bool ClearReplySettingsAfterRouting { get; set; } = true;
    }
}
