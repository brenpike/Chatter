using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Microsoft.Extensions.Configuration;

namespace Chatter.MessageBrokers.Recovery.Options
{
    public class RecoveryOptions
    {
        public int MaxRetryAttempts { get; internal set; }

        // INVARIANT: the documented section is 'Chatter:MessageBrokers:Recovery:CircuitBreaker'
        // (CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName), so the one documented key must bind from
        // both the standalone circuit breaker entry point and this nested one. Without the alias the binder would
        // look for a child key named 'CircuitBreakerOptions', which the module documents nowhere.
        [ConfigurationKeyName("CircuitBreaker")]
        public CircuitBreakerOptions CircuitBreakerOptions { get; internal set; }
    }
}
