using Chatter.CQRS.Events;
using System;

namespace CarRental.Application.Events
{
    public class CarRentalAggregateChangedEvent : IEvent
    {
        public Guid Id { get; set; }
        public string Vendor { get; set; }
        public string Airport { get; set; }
        public DateTime From { get; set; }
        public DateTime Until { get; set; }
        public Guid ReservationId { get; set; }
    }
}
