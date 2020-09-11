using Chatter.CQRS.Events;
using Chatter.MessageBrokers;
using System;

namespace CarRental.Application.IntegrationEvents
{
    //no subscriber defined in attribute as tthe car rental service only dispatches the topic and doesn't listen to this subscription
    [BrokeredMessage("book-trip-saga/rental-car-booked")]
    public class RentalCarBookedEvent : IEvent
    {
        public Guid Id { get; set; }
        public Guid ReservationId { get; set; }
    }
}
