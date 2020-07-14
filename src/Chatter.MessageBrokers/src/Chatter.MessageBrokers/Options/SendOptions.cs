namespace Chatter.MessageBrokers.Options
{
    public class SendOptions
    {
        bool RefreshTimeToLive { get; set; } = true;
        bool ClearReplySettings { get; set; } = true;
    }
}
