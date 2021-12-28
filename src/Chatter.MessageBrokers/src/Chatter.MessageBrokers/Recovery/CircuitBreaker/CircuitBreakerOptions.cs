namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public class CircuitBreakerOptions
    {
        public int OpenToHalfOpenWaitTimeInSeconds { get; set; }
        public int ConcurrentHalfOpenAttempts { get; set; }
        public int NumberOfFailuresBeforeOpen { get; set; }
        public int NumberOfHalfOpenSuccessesToClose { get; set; }
        public int SecondsOpenBeforeCriticalFailureNotification { get; set; }
    }
}
