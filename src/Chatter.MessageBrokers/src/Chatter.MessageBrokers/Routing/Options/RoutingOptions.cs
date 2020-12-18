using Chatter.CQRS.Context;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Routing.Options
{
    public abstract class RoutingOptions : IRoutingOptions
    {
        public const string DefaultContentType = "application/json";

        public RoutingOptions()
            : this(new Dictionary<string, object>())
        {
            Container = new ContextContainer();
        }

        public RoutingOptions(IDictionary<string, object> context)
        {
            Container = new ContextContainer();
            MessageContext = context;

            if (MessageContext.TryGetValue(MessageBrokers.MessageContext.ContentType, out var contentType))
            {
                ContentType = (string)contentType;
            }
        }

        public string MessageId { get; set; }
        public string ContentType { get; set; } = DefaultContentType;

        internal IDictionary<string, object> MessageContext { get; }

        public void SetCorrelationId(string correlationId)
        {
            this.WithMessageContext(MessageBrokers.MessageContext.CorrelationId, correlationId);
        }

        public ContextContainer Container { get; }
    }
}
