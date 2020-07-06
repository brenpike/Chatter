using System;

namespace Samples.SharedKernel.Dtos
{
    public class FlightLeg
    {
        public Guid Id { get; set; }
        public string FlightNo { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public DateTime Date { get; set; }
    }
}
