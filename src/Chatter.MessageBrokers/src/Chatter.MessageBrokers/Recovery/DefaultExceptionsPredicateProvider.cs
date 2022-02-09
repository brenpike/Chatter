using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    public class DefaultExceptionsPredicateProvider : IRetryExceptionPredicatesProvider, ICircuitBreakerExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            yield return new Predicate<Exception>(e => e.GetType() == typeof(BrokeredMessageReceiverException) && ((BrokeredMessageReceiverException)e).IsTransient);
            yield return new Predicate<Exception>(e => e.GetType() == typeof(TimeoutException));
            yield return new Predicate<Exception>(e => e.ToString().ToLowerInvariant().Contains("retry"));
            yield return new Predicate<Exception>(e => e.ToString().ToLowerInvariant().Contains("timeout"));
            yield return new Predicate<Exception>(e => e.ToString().ToLowerInvariant().Contains("time out"));
            yield return new Predicate<Exception>(e => e.ToString().ToLowerInvariant().Contains("rerun"));
            yield return new Predicate<Exception>(e => e.ToString().ToLowerInvariant().Contains("internal server error"));
            yield return new Predicate<Exception>(e => e.ToString().ToLowerInvariant().Contains("waiting"));
            yield return new Predicate<Exception>(e => e.ToString().ToLowerInvariant().Contains("wait until"));
            yield return new Predicate<Exception>(e => e.ToString().ToLowerInvariant().Contains("service unavailable"));
        }
    }
}
