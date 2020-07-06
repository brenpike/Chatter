using System;

namespace Samples.SharedKernel.Dtos
{
    public class HotelBooking
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
