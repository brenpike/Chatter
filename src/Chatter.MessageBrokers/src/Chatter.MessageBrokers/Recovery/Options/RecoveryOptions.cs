namespace Chatter.MessageBrokers.Recovery.Options
{
    public class RecoveryOptions
    {
        public int MaxRetryAttempts { get; set; } = 5;
        public int ConstantDelayInMilliseconds { get; set; } = 2000;
    }
}
