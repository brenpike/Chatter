using System;

namespace Samples.SharedKernel.Dtos
{
    public class TravelBooking
    {
        public Guid Id { get; set; }
        public CarRental Car { get; set; }
        public HotelBooking Hotel { get; set; }
        public FlightBooking Flight { get; set; }
    }
}
