using Chatter.CQRS.Events;
using System.Collections.Generic;

namespace Samples.SharedKernel.Interfaces
{
    public interface IAggregate<TId> : IEntity<TId>
    {
        IEnumerable<IDomainEvent> DomainEvents { get; }
    }
}
