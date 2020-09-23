using CarRental.Domain.ValueObjects;
using Samples.SharedKernel;

namespace CarRental.Domain.Entities
{
    public class Airport : Entity<string>
    {
        public Airport(string airportCode)
        {
            AirportCode = new AirportCode(airportCode);
        }

        public AirportCode AirportCode { get; }
    }
}
