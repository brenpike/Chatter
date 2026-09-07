using Chatter.MessageBrokers.Exceptions;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    /// <summary>
    /// Classifies a receive failure as circuit-tripping by EXCEPTION TYPE only. A transient
    /// <see cref="BrokeredMessageReceiverException"/> trips the circuit; nothing else does. Message TEXT is never
    /// inspected, because the circuit breaker gates every message on its receiver, so a single message whose
    /// handler exception echoed body content could otherwise deny service to all the rest.
    /// </summary>
    internal sealed class DefaultCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            yield return new Predicate<Exception>(e => e is BrokeredMessageReceiverException exception && exception.IsTransient);
        }
    }
}
