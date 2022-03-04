using System;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public class CircuitBreakerOpenException : Exception
    {
        private const string _openMessage = "Circuit breaker is still in the OPEN state. Action was not executed.";

        public CircuitBreakerOpenException(Exception lastException)
            : base(_openMessage, lastException)
        { }
    }
}
