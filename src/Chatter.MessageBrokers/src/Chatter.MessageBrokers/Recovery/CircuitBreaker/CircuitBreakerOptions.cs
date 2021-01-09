namespace Chatter.MessageBrokers.Recovery.CircuitBreaker
{
    public class CircuitBreakerOptions
    {
        public int OpenToHalfOpenWaitTimeInSeconds { get; set; } = 15;
        public int ConcurrentHalfOpenAttempts { get; set; } = 1;
        public int NumberOfFailuresBeforeOpen { get; set; } = 5;
        public int NumberOfHalfOpenSuccessesToClose { get; set; } = 3;
        public int SecondsOpenBeforeCriticalFailureNotification { get; set; } = 30;
    }
}
