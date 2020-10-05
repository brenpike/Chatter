using System;

namespace TravelBooking.Application.DTO
{
    public class HotelBookingDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public DateTime CheckIn { get; set; }
        public DateTime CheckOut { get; set; }
        public Guid ReservationId { get; set; }
    }
}
