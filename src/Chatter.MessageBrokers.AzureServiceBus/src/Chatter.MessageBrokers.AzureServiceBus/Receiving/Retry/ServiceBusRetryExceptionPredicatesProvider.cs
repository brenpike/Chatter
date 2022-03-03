using Chatter.MessageBrokers.Recovery.Retry;
using Microsoft.Azure.ServiceBus;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving.Retry
{
    internal sealed class ServiceBusRetryExceptionPredicatesProvider : IRetryExceptionPredicatesProvider
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
