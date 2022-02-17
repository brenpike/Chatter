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
            yield return new Predicate<Exception>(e => e is BrokeredMessageReceiverException exception && exception.IsTransient);
            yield return new Predicate<Exception>(e => e.Message.ToLowerInvariant().Contains("retry"));
            yield return new Predicate<Exception>(e => e.Message.ToLowerInvariant().Contains("timeout"));
            yield return new Predicate<Exception>(e => e.Message.ToLowerInvariant().Contains("time out"));
            yield return new Predicate<Exception>(e => e.Message.ToLowerInvariant().Contains("rerun"));
            yield return new Predicate<Exception>(e => e.Message.ToLowerInvariant().Contains("internal server error"));
            yield return new Predicate<Exception>(e => e.Message.ToLowerInvariant().Contains("waiting"));
            yield return new Predicate<Exception>(e => e.Message.ToLowerInvariant().Contains("wait until"));
            yield return new Predicate<Exception>(e => e.Message.ToLowerInvariant().Contains("service unavailable"));
        }
    }
}
