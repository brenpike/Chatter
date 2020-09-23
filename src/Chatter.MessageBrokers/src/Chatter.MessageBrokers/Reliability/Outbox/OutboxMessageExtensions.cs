using Chatter.MessageBrokers.Sending;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public static class OutboxMessageExtensions
    {
        internal static OutboundBrokeredMessage AsOutboundBrokeredMessage(this OutboxMessage outboxMessage, IBrokeredMessageBodyConverter brokeredMessageBodyConverter)
        {
            var appProps = JsonConvert.DeserializeObject<IDictionary<string, object>>(outboxMessage.StringifiedApplicationProperties);
            return new OutboundBrokeredMessage(outboxMessage.MessageId, outboxMessage.Body, appProps, outboxMessage.Destination, brokeredMessageBodyConverter);
        }
    }
}
