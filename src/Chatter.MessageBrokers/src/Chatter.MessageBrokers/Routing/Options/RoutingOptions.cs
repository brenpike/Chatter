using Chatter.CQRS.Context;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Options
{
    public abstract class RoutingOptions : IRoutingOptions
    {
        public const string DefaultContentType = "application/json";

        public RoutingOptions()
        { 
            Container = new ContextContainer();
            ApplicationProperties = new Dictionary<string, object>();
        }

        public string MessageId { get; set; }
        public string ContentType { get; set; } = DefaultContentType;

        internal IDictionary<string, object> ApplicationProperties { get; }

        public void SetCorrelationId(string correlationId)
        {
            this.SetApplicationProperty(MessageBrokers.MessageContext.CorrelationId, correlationId);
        }

        public ContextContainer Container { get; }
    }
}
