using Chatter.MessageBrokers.Recovery.CircuitBreaker;

namespace Chatter.Testing.Core.Creators.MessageBrokers.Recovery
{
    public class CircuitBreakerOptionsCreator : Creator<CircuitBreakerOptions>
    {
        public CircuitBreakerOptionsCreator(INewContext newContext, CircuitBreakerOptions creation = default)
            : base(newContext, creation)
        {
            // INVARIANT: timing-related options default to zero so the CircuitBreaker runtime never
            // performs a real wall-clock wait (Task.Delay(0)) during characterization tests.
            Creation = new CircuitBreakerOptions
            {
                OpenToHalfOpenWaitTimeInSeconds = 0,
                ConcurrentHalfOpenAttempts = 1,
                NumberOfFailuresBeforeOpen = 1,
                NumberOfHalfOpenSuccessesToClose = 1,
                SecondsOpenBeforeCriticalFailureNotification = 0
            };
        }

        public CircuitBreakerOptionsCreator WithFailuresBeforeOpen(int numberOfFailures)
        {
            Creation.NumberOfFailuresBeforeOpen = numberOfFailures;
            return this;
        }

        public CircuitBreakerOptionsCreator WithHalfOpenSuccessesToClose(int numberOfSuccesses)
        {
            Creation.NumberOfHalfOpenSuccessesToClose = numberOfSuccesses;
            return this;
        }

        public CircuitBreakerOptionsCreator WithConcurrentHalfOpenAttempts(int numberOfAttempts)
        {
            Creation.ConcurrentHalfOpenAttempts = numberOfAttempts;
            return this;
        }
    }
}
