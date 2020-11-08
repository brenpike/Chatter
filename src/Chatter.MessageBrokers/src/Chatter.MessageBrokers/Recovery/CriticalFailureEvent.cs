using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.Recovery
{
    public class CriticalFailureEvent : IEvent
    {
        public FailureContext Context { get; set; }
    }
}
