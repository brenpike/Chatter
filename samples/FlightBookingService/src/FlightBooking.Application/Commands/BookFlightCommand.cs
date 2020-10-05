using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using FlightBooking.Application.DTO;
using System;
using System.Collections.Generic;

namespace FlightBooking.Application.Commands
{
    [BrokeredMessage("book-trip-saga/3/book-flight", "book-trip-saga/3/book-flight")]
    public class BookFlightCommand : ICommand
    {
        public Guid Id { get; set; }
        public string BookingClass { get; set; }
        public List<FlightLegDto> Legs { get; set; }
        public Guid ReservationId { get; set; }
    }
}
