using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    internal sealed class ConfigRetryExceptionPredicatesProvider : IRetryExceptionPredicatesProvider
    {
        private readonly IEnumerable<Predicate<Exception>> _predicates;
        public ConfigRetryExceptionPredicatesProvider(IEnumerable<Predicate<Exception>> predicates) => _predicates = predicates;
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates() => _predicates;
    }
}
