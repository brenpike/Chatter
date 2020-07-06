using System;
using System.Collections.Generic;

namespace Samples.SharedKernel.Dtos
{
    public class FlightBooking
    {
        public Guid Id { get; set; }
        public string BookingClass { get; set; }
        public List<FlightLeg> Legs { get; set; }
        public Guid ReservationId { get; set; }
    }
}
