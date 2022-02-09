using System;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    internal class RetryExceptionEvaluator : IRetryExceptionEvaluator
    {
        private readonly IEnumerable<Predicate<Exception>> _predicates;
        public RetryExceptionEvaluator(IEnumerable<IRetryExceptionPredicatesProvider> predicatesProviders)
        {
            _predicates = predicatesProviders?.SelectMany(pp => pp?.GetExceptionPredicates()) ?? new List<Predicate<Exception>>();
        }

        public bool ShouldRetry(Exception e) => _predicates.Any(ex => ex?.Invoke(e) ?? false);
    }
}
