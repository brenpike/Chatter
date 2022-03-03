using System;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    internal class CircuitBreakerExceptionEvaluator : ICircuitBreakerExceptionEvaluator
    {
        private readonly IEnumerable<Predicate<Exception>> _predicates;
        public CircuitBreakerExceptionEvaluator(IEnumerable<ICircuitBreakerExceptionPredicatesProvider> predicatesProviders) 
            => _predicates = predicatesProviders?.SelectMany(pp => pp?.GetExceptionPredicates()) ?? new List<Predicate<Exception>>();

        public bool ShouldTrip(Exception e) => _predicates.Any(ex => ex?.Invoke(e) ?? false);
    }
}
