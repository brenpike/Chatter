using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    internal sealed class ConfigCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
    {
        private readonly IEnumerable<Predicate<Exception>> _predicates;
        public ConfigCircuitBreakerExceptionPredicatesProvider(IEnumerable<Predicate<Exception>> predicates) => _predicates = predicates;
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates() => _predicates;
    }
}
