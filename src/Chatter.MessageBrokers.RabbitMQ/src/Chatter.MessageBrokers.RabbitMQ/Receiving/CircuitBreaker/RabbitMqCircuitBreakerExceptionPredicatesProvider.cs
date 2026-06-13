using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

namespace Chatter.MessageBrokers.RabbitMQ.Receiving.CircuitBreaker
{
    /// <summary>
    /// Classifies transient RabbitMQ faults as circuit-breakable so the shared recovery strategy trips the
    /// circuit breaker on sustained transient failure. Non-transient faults (e.g. serialization or
    /// precondition failures) are not matched and therefore do not contribute to tripping the breaker.
    /// </summary>
    internal sealed class RabbitMqCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
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
