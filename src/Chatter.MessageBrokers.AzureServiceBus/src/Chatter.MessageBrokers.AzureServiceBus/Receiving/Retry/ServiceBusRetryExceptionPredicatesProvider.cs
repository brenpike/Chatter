using Chatter.MessageBrokers.Recovery.Retry;
using Microsoft.Azure.ServiceBus;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving.Retry
{
    public class ServiceBusRetryExceptionPredicatesProvider : IRetryExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            yield return new Predicate<Exception>(e => e.GetType() == typeof(ServiceBusException) && ((ServiceBusException)e).IsTransient);
            yield return new Predicate<Exception>(e => e.GetType() == typeof(ServiceBusCommunicationException) && ((ServiceBusCommunicationException)e).IsTransient);
            yield return new Predicate<Exception>(e => e.GetType() == typeof(ServerBusyException) && ((ServerBusyException)e).IsTransient);
            yield return new Predicate<Exception>(e => e.GetType() == typeof(ServiceBusTimeoutException) && ((ServiceBusTimeoutException)e).IsTransient);
        }
    }
}
