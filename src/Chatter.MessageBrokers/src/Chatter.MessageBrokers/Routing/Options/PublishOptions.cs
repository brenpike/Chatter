using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Options
{
    public class PublishOptions : RoutingOptions
    {
        public PublishOptions() { }
        public PublishOptions(IDictionary<string, object> messageContext) : base(messageContext) { }
    }
}
