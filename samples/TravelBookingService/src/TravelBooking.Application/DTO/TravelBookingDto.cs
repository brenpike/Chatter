using System;

namespace TravelBooking.Application.DTO
{
    public class TravelBookingDto
    {
        public Guid Id { get; set; }
        public CarRentalDto Car { get; set; }
        public HotelBookingDto Hotel { get; set; }
        public FlightBookingDto Flight { get; set; }
    }
}
