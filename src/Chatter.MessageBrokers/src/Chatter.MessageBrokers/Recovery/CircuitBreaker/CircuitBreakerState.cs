namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public enum CircuitBreakerState
    {
        Closed,
        HalfOpen,
        Open
    }
}
