using Chatter.CQRS.Events;
using Samples.SharedKernel.Interfaces;
using System.Collections.Generic;

namespace CarRental.Application.Services
{
    public interface IEventMapper
    {
        IEvent Map(IDomainEvent @event);
        IEnumerable<IEvent> MapAll(IEnumerable<IDomainEvent> events);
    }
}
