using Chatter.CQRS.Events;
using System;

namespace CarRental.Application.Events
{
    public class OutboxChangedEvent : IEvent
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
