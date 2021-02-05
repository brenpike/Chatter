using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Options
{
    public class PublishOptions : RoutingOptions
    {
        public PublishOptions() { }
        private PublishOptions(IDictionary<string, object> messageContext) : base(messageContext) { }
        internal static PublishOptions Create(IDictionary<string, object> messageContext) => new PublishOptions(messageContext);

        public PublishOptions Merge(PublishOptions optionsToMerge) => Merge(optionsToMerge?.MessageContext) as PublishOptions;
    }
}
