using System;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public sealed class OutboxMessage
    {
        public string MessageId { get; set; }
        public string Destination { get; set; }
        public byte[] Body { get; set; }
        public string StringifiedApplicationProperties { get; set; }
        public string StringifiedMessage { get; set; }
        public DateTime SentToOutboxAtUtc { get; set; }
        public DateTime? ProcessedFromOutboxAtUtc { get; set; }
        public Guid BatchId { get; set; }
    }
}
