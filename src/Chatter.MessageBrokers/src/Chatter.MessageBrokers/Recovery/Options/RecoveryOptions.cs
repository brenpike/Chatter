using Chatter.MessageBrokers.Recovery.CircuitBreaker;

namespace Chatter.MessageBrokers.Recovery.Options
{
    public class RecoveryOptions
    {
        public int MaxRetryAttempts { get; internal set; }
        public CircuitBreakerOptions CircuitBreakerOptions { get; internal set; }
    }
}
