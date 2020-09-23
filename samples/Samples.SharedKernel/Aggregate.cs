using Samples.SharedKernel.Interfaces;
using System.Collections.Generic;

namespace Samples.SharedKernel
{
    public abstract class Aggregate<TId> : Entity<TId>, IAggregate<TId>
    {
        private List<IDomainEvent> _domainEvents;
        public IEnumerable<IDomainEvent> DomainEvents => _domainEvents?.AsReadOnly();

        public void AddDomainEvent(IDomainEvent eventItem)
        {
            _domainEvents ??= new List<IDomainEvent>();
            _domainEvents.Add(eventItem);
        }

        public void RemoveDomainEvent(IDomainEvent eventItem)
        {
            _domainEvents?.Remove(eventItem);
        }

        public void ClearDomainEvents()
        {
            _domainEvents?.Clear();
        }
    }
}
