using Chatter.MessageBrokers.Recovery.Retry;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

namespace Chatter.MessageBrokers.RabbitMQ.Receiving.Retry
{
    /// <summary>
    /// Classifies transient RabbitMQ faults as retryable so the shared recovery strategy retries a
    /// transient receive failure. Non-transient faults (e.g. serialization or precondition failures) are
    /// not matched and therefore are not retried.
    /// </summary>
    internal sealed class RabbitMqRetryExceptionPredicatesProvider : IRetryExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            yield return new Predicate<Exception>(e => e is BrokerUnreachableException);
            yield return new Predicate<Exception>(e => e is AlreadyClosedException);
            yield return new Predicate<Exception>(e => e is OperationInterruptedException);
            yield return new Predicate<Exception>(e => e is ConnectFailureException);
            yield return new Predicate<Exception>(e => e is SocketException);
            yield return new Predicate<Exception>(e => e is IOException);
        }
    }
}
