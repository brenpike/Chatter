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
            : this(optionsToMerge?.MessageContext)
            => MessageId = optionsToMerge?.MessageId;

        public string MessageId { get; set; }
        public string ContentType
        {
            get
            {
                // INVARIANT: a stored content type is read by a KIND TEST, so a value of another kind reads as null
                // rather than throwing InvalidCastException from this getter, and the dispatcher then refuses the send
                // as it refuses a blank content type. Oracles: MustReadContentTypeAsNullWhenTheStoredKindIsNotAString
                // and MustRefuseTheSendAsContentTypeRequiredWhenTheContentTypeIsNotAString; restoring the
                // `(string)contentType` cast reddens both.
                if (MessageContext.TryReadMessageContext<string>(MessageBrokers.MessageContext.ContentType, out var contentType))
                {
                    return contentType;
                }

                return MessageContext.ContainsKey(MessageBrokers.MessageContext.ContentType) ? null : DefaultContentType;
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
