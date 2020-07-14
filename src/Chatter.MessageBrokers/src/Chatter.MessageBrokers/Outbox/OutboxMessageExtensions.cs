using Chatter.MessageBrokers.Sending;

namespace Chatter.MessageBrokers.Outbox
{
    public static class OutboxMessageExtensions
    {
        internal static OutboundBrokeredMessage AsOutboundBrokeredMessage(this OutboxMessage outboxMessage, IBrokeredMessageBodyConverter brokeredMessageBodyConverter)
        {
            return new OutboundBrokeredMessage(outboxMessage.MessageId, outboxMessage.Body, outboxMessage.ApplicationProperties, outboxMessage.Destination, brokeredMessageBodyConverter);
        }
    }
}
