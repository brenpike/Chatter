using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using FlightBooking.Application.DTO;
using System;
using System.Collections.Generic;

namespace FlightBooking.Application.Commands
{
    [BrokeredMessage("book-trip-saga/3/cancel-flight", "book-trip-saga/3/cancel-flight")]
    public class CancelFlightBookingCommand : ICommand
    {
        public Guid Id { get; set; }
        public string BookingClass { get; set; }
        public List<FlightLegDto> Legs { get; set; }
        public Guid ReservationId { get; set; }
    }
}
