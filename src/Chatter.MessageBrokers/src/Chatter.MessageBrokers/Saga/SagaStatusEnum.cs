namespace Chatter.MessageBrokers.Saga
{
    public enum SagaStatusEnum : byte
    {
        NotStarted = 1,
        InProgress = 2,
        Success = 3,
        Failed = 4,
        Cancelled = 5
    }
}
