using Chatter.CQRS.Events;
using Chatter.MessageBrokers;
using System;

namespace HotelBooking.Application.IntegrationEvents
{
    //[BrokeredMessage("book-trip-saga/rental-car-booked", "hotel")]
    public class RentalCarBookedEvent : IEvent
    {
        public Guid Id { get; set; }
        public Guid ReservationId { get; set; }
    }
}
