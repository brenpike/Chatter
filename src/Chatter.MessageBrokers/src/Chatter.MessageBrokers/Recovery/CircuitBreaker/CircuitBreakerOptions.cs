namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public class CircuitBreakerOptions
    {
        public int OpenToHalfOpenWaitTimeInSeconds { get; internal set; }
        public int ConcurrentHalfOpenAttempts { get; internal set; }
        public int NumberOfFailuresBeforeOpen { get; internal set; }
        public int NumberOfHalfOpenSuccessesToClose { get; internal set; }
        public int SecondsOpenBeforeCriticalFailureNotification { get; internal set; }
    }
}
