using System;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public sealed class OutboxMessage
    {
        public int Id { get; set; }
        public string MessageId { get; set; }
        public string Destination { get; set; }
        public string MessageContext { get; set; }
        public string MessageBody { get; set; }
        public string MessageContentType { get; set; }
        public DateTime SentToOutboxAtUtc { get; set; }
        public DateTime? ProcessedFromOutboxAtUtc { get; set; }
        public Guid BatchId { get; set; }
    }
}
