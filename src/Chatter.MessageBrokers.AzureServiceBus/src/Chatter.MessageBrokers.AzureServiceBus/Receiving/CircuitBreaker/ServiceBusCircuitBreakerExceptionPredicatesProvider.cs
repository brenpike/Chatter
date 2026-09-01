using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving.CircuitBreaker
{
    internal sealed class ServiceBusCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.IsTransient);
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.Reason == ServiceBusFailureReason.ServiceCommunicationProblem);
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.Reason == ServiceBusFailureReason.ServiceBusy);
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.Reason == ServiceBusFailureReason.ServiceTimeout);
        }
    }
}
