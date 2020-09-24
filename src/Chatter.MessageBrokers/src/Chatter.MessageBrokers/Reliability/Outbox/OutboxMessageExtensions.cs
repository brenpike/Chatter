using Chatter.MessageBrokers.Sending;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public static class OutboxMessageExtensions
    {
        internal static OutboundBrokeredMessage AsOutboundBrokeredMessage(this OutboxMessage outboxMessage, IDictionary<string, object> appProperties, IBrokeredMessageBodyConverter brokeredMessageBodyConverter) 
            => new OutboundBrokeredMessage(outboxMessage.MessageId, outboxMessage.Body, appProperties, outboxMessage.Destination, brokeredMessageBodyConverter);
    }
}
