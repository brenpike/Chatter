namespace Chatter.MessageBrokers.Recovery
{
    public enum RecoveryState
    {
        Retrying = 1,
        RecoveryActionExecuted = 2,
        DeadLetter = 3
    }
}
