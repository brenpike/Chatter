namespace Chatter.MessageBrokers.Routing.Options
{
    public static class RoutingOptionsExtensions
    {
        public static RoutingOptions SetApplicationProperty(this RoutingOptions routingOptions, string key, object value)
        {
            routingOptions.MessageContext[key] = value;
            return routingOptions;
        }
    }
}
