using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Outbox
{
    internal sealed class OutboxMessage
    {
        public string MessageId { get; set; }
        public string Destination { get; set; }
        public byte[] Body { get; set; }
        public IDictionary<string, object> ApplicationProperties { get; set; }
        public string StringifiedMessage { get; set; }
        public DateTime SentToOutboxAtUtc { get; set; }
        public DateTime? ProcessedFromOutboxAtUtc { get; set; }
    }
}
