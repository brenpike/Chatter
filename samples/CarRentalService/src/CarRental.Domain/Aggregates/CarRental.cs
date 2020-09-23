using CarRental.Domain.DomainEvents;
using CarRental.Domain.ValueObjects;
using Samples.SharedKernel;
using System;

namespace CarRental.Domain.Aggregates
{
    public class CarRental : Aggregate<Guid>
    {
        private string _vendor;
        private string _airport;
        private DateTime _from;
        private DateTime _until;
        private Guid _reservationId;

        public CarRental()
        {
            Id = Guid.NewGuid();
        }

        public Guid Reserve(string airportCode, string vendor, DateTime from, DateTime until)
        {
            _vendor = vendor;
            _airport = new AirportCode(airportCode).Code;
            _from = from;
            _until = until;
            _reservationId = Guid.NewGuid();

            AddDomainEvent(new CarRentalBooked() { Id = Id, ReservationId = _reservationId });

            return _reservationId;
        }
    }
}
