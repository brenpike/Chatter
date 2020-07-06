namespace Chatter.MessageBrokers.Options
{
    public class NextOptions
    {
        bool RefreshTimeToLive { get; set; } = true;
        bool ClearReplySettings { get; set; } = true;
    }
}
