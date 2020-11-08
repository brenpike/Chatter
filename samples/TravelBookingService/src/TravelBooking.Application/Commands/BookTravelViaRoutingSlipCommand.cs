using Chatter.CQRS.Commands;
using System;
using TravelBooking.Application.DTO;

namespace TravelBooking.Application.Commands
{
    public class BookTravelViaRoutingSlipCommand : ICommand
    {
        public Guid Id { get; set; }
        public CarRentalDto Car { get; set; }
        public HotelBookingDto Hotel { get; set; }
        public FlightBookingDto Flight { get; set; }
    }
}
