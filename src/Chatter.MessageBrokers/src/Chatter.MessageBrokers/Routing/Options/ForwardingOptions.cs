namespace Chatter.MessageBrokers.Routing.Options
{
    public class ForwardingOptions : RoutingOptions
    {
        public bool RefreshTimeToLive { get; set; } = true;
    }
}
