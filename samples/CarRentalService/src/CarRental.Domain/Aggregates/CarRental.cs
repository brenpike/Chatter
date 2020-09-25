using CarRental.Domain.DomainEvents;
using CarRental.Domain.ValueObjects;
using Samples.SharedKernel;
using System;

namespace CarRental.Domain.Aggregates
{
    public class CarRental : Aggregate<Guid>
    {
        public string Vendor { get; internal set; }
        public string Airport { get; internal set; }
        public DateTime From { get; internal set; }
        public DateTime Until { get; internal set; }
        public Guid ReservationId { get; internal set; }

        public CarRental()
        {
            Id = Guid.NewGuid();
        }

        public Guid Reserve(string airportCode, string vendor, DateTime from, DateTime until)
        {
            Vendor = vendor;
            Airport = new AirportCode(airportCode).Code;
            From = from;
            From = until;
            ReservationId = Guid.NewGuid();

            AddDomainEvent(new CarRentalBooked() { Id = Id, ReservationId = ReservationId });

            return ReservationId;
        }
    }
}
