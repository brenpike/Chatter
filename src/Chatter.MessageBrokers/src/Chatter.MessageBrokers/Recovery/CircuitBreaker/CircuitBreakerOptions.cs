using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public class CircuitBreakerOptions
    {
        public int OpenToHalfOpenWaitTimeInSeconds { get; internal set; }
        public int ConcurrentHalfOpenAttempts { get; internal set; }
        public int NumberOfFailuresBeforeOpen { get; internal set; }
        public int NumberOfHalfOpenSuccessesToClose { get; internal set; }
        public int SecondsOpenBeforeCriticalFailureNotification { get; internal set; }

        // INVARIANT: every check runs and a single failure names every offending value. An operator who corrected one
        // option, redeployed, and only then discovered the next would pay a deployment per invalid value.
        // The minimums are what CircuitBreaker itself can run with: ConcurrentHalfOpenAttempts becomes
        // new SemaphoreSlim(n, n), which rejects a maxCount below 1; the two counters are compared with >= as
        // thresholds, so a threshold below 1 is already met before anything happens; and the two second counts become
        // a Task.Delay and a Timer due time, both of which reject a negative TimeSpan.
        internal void Validate()
        {
            var violations = new List<string>();
            AddViolationWhenBelowMinimum(violations, nameof(OpenToHalfOpenWaitTimeInSeconds), OpenToHalfOpenWaitTimeInSeconds, 0);
            AddViolationWhenBelowMinimum(violations, nameof(ConcurrentHalfOpenAttempts), ConcurrentHalfOpenAttempts, 1);
            AddViolationWhenBelowMinimum(violations, nameof(NumberOfFailuresBeforeOpen), NumberOfFailuresBeforeOpen, 1);
            AddViolationWhenBelowMinimum(violations, nameof(NumberOfHalfOpenSuccessesToClose), NumberOfHalfOpenSuccessesToClose, 1);
            AddViolationWhenBelowMinimum(violations, nameof(SecondsOpenBeforeCriticalFailureNotification), SecondsOpenBeforeCriticalFailureNotification, 0);

            if (violations.Count > 0)
            {
                throw new CircuitBreakerOptionsValidationException(violations);
            }
        }

        private static void AddViolationWhenBelowMinimum(ICollection<string> violations, string optionName, int configuredValue, int minimum)
        {
            if (configuredValue >= minimum)
            {
                return;
            }

            violations.Add($"'{optionName}' is {configuredValue}, but the lowest value the circuit breaker can run with is {minimum}");
        }
    }
}
