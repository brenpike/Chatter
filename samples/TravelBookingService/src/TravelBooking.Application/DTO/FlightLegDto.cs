using System;

namespace TravelBooking.Application.DTO
{
    public class FlightLegDto
    {
        public Guid Id { get; set; }
        public string FlightNo { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public DateTime Date { get; set; }
    }
}
