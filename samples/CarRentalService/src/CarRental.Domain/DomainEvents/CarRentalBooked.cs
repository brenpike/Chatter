using Samples.SharedKernel.Interfaces;
using System;

namespace CarRental.Domain.DomainEvents
{
    public class CarRentalBooked : IDomainEvent
    {
        public Guid Id { get; set; }
        public Guid ReservationId { get; set; }
    }
}
