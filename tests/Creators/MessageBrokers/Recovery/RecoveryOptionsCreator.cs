using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;

namespace Chatter.Testing.Core.Creators.MessageBrokers.Recovery
{
    public class RecoveryOptionsCreator : Creator<RecoveryOptions>
    {
        public RecoveryOptionsCreator(INewContext newContext, RecoveryOptions creation = default)
            : base(newContext, creation)
        {
            Creation = new RecoveryOptions
            {
                MaxRetryAttempts = 3
            };
        }

        public RecoveryOptionsCreator WithMaxRetryAttempts(int maxRetryAttempts)
        {
            Creation.MaxRetryAttempts = maxRetryAttempts;
            return this;
        }

        public RecoveryOptionsCreator WithCircuitBreakerOptions(CircuitBreakerOptions circuitBreakerOptions)
        {
            Creation.CircuitBreakerOptions = circuitBreakerOptions;
            return this;
        }
    }
}
