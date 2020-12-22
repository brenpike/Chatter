using Chatter.CQRS.Context;
using System;
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
        }

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

        public ContextContainer Container { get; }
    }
}
