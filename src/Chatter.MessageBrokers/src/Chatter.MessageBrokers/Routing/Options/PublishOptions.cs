using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Options
{
    public class PublishOptions : RoutingOptions
    {
        public PublishOptions() { }
        private PublishOptions(IDictionary<string, object> messageContext) : base(messageContext) { }
        internal static PublishOptions Create(IDictionary<string, object> messageContext)
            => new PublishOptions(messageContext is null ? null : new Dictionary<string, object>(messageContext));

        public PublishOptions Merge(PublishOptions optionsToMerge)
        {
            Merge(optionsToMerge?.MessageContext);
            if (!string.IsNullOrWhiteSpace(optionsToMerge?.MessageId))
            {
                this.MessageId = optionsToMerge.MessageId;
            }
            return this;
        }
    }
}
