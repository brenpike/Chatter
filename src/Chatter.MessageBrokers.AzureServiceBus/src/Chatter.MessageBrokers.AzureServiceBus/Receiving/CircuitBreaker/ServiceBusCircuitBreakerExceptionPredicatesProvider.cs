using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Microsoft.Azure.ServiceBus;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving.CircuitBreaker
{
    public class ServiceBusCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.IsTransient);
            yield return new Predicate<Exception>(e => e is ServiceBusCommunicationException exception && exception.IsTransient);
            yield return new Predicate<Exception>(e => e is ServerBusyException exception && exception.IsTransient);
            yield return new Predicate<Exception>(e => e is ServiceBusTimeoutException exception && exception.IsTransient);
        }
    }
}
