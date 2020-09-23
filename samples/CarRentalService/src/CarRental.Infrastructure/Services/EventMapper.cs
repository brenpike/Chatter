using CarRental.Application.IntegrationEvents;
using CarRental.Application.Services;
using CarRental.Domain.DomainEvents;
using Chatter.CQRS.Events;
using Samples.SharedKernel.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace CarRental.Infrastructure.Services
{
    public class EventMapper : IEventMapper
    {
        public IEnumerable<IEvent> MapAll(IEnumerable<IDomainEvent> events)
            => events.Select(Map);

        public IEvent Map(IDomainEvent @event)
        {
            switch (@event)
            {
                case CarRentalBooked e:
                    return new RentalCarBookedEvent() { Id = e.Id, ReservationId = e.ReservationId };

                    //TODO: add other event mappings
            }

            return null;
        }
    }
}
