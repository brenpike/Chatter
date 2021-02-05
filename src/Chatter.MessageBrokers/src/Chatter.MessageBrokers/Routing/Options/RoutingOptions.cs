using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Options
{
    public abstract class RoutingOptions : IRoutingOptions
    {
        public const string DefaultContentType = "application/json";

        public RoutingOptions()
            : this(new Dictionary<string, object>()) { }

        public RoutingOptions(IDictionary<string, object> context)
            => MessageContext = context ?? new Dictionary<string, object>();

        public RoutingOptions(RoutingOptions optionsToMerge)
            : this(optionsToMerge?.MessageContext) { }

        public string MessageId { get; set; }
        public string ContentType
        {
            get
            {
                if (MessageContext.TryGetValue(MessageBrokers.MessageContext.ContentType, out var contentType))
                {
                    return (string)contentType;
                }
                else
                {
                    return DefaultContentType;
                }
            }
            set
            {
                this.WithMessageContext(MessageBrokers.MessageContext.ContentType, value);
            }
        }

        internal IDictionary<string, object> MessageContext { get; }

        public void SetCorrelationId(string correlationId)
            => this.WithMessageContext(MessageBrokers.MessageContext.CorrelationId, correlationId);

        public void UseMessagingInfrastructure(Func<InfrastructureTypes, string> infrastructureSelector)
            => this.WithMessageContext(MessageBrokers.MessageContext.InfrastructureType, infrastructureSelector(new InfrastructureTypes()));

        internal virtual RoutingOptions Merge(IDictionary<string, object> contextToMerge)
        {
            if (contextToMerge is null)
            {
                return this;
            }

            foreach (var kvp in contextToMerge)
            {
                MessageContext[kvp.Key] = kvp.Value;
            }

            return this;
        }
    }
}
