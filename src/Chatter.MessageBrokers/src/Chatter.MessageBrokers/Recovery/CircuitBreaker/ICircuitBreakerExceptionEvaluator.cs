using System;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public interface ICircuitBreakerExceptionEvaluator
    {
        bool ShouldTrip(Exception e);
    }
}
