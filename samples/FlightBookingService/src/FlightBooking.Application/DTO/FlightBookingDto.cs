using System;
using System.Collections.Generic;

namespace FlightBooking.Application.DTO
{
    public class FlightBookingDto
    {
        public Guid Id { get; set; }
        public string BookingClass { get; set; }
        public List<FlightLegDto> Legs { get; set; }
        public Guid ReservationId { get; set; }
    }
}
