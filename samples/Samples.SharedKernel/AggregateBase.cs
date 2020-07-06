using Chatter.CQRS.Events;
using Samples.SharedKernel.Interfaces;
using System.Collections.Generic;

namespace Samples.SharedKernel
{
    public abstract class AggregateBase<TId> : EntityBase<TId>, IAggregate<TId>
    {
        private List<IEvent> _domainEvents;
        public IReadOnlyCollection<IEvent> DomainEvents => _domainEvents?.AsReadOnly();

        public void AddDomainEvent(IEvent eventItem)
        {
            _domainEvents ??= new List<IEvent>();
            _domainEvents.Add(eventItem);
        }

        public void RemoveDomainEvent(IEvent eventItem)
        {
            _domainEvents?.Remove(eventItem);
        }

        public void ClearDomainEvents()
        {
            _domainEvents?.Clear();
        }
    }
}
