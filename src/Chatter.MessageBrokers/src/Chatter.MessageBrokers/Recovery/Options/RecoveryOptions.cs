using Chatter.MessageBrokers.Recovery.CircuitBreaker;

namespace Chatter.MessageBrokers.Recovery.Options
{
    public class RecoveryOptions
    {
        public int MaxRetryAttempts { get; set; }
        public CircuitBreakerOptions CircuitBreakerOptions { get; set; }
    }
}
