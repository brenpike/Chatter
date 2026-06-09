using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving.CircuitBreaker
{
    internal sealed class ServiceBusCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
    {
        // Azure.Messaging.ServiceBus 7.20.1 collapses the legacy distinct exception subtypes
        // (ServiceBusException/ServiceBusCommunicationException/ServerBusyException/ServiceBusTimeoutException)
        // into a single ServiceBusException carrying IsTransient and a ServiceBusFailureReason. The reason
        // map preserves the legacy classification: communication-problem/busy/timeout were the transient
        // subtypes, and ServiceBusException.IsTransient is true for exactly those reasons.
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.IsTransient);
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.Reason == ServiceBusFailureReason.ServiceCommunicationProblem);
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.Reason == ServiceBusFailureReason.ServiceBusy);
            yield return new Predicate<Exception>(e => e is ServiceBusException exception && exception.Reason == ServiceBusFailureReason.ServiceTimeout);
        }
    }
}
