namespace Chatter.MessageBrokers.Saga
{
    public class SagaStatus
    {
        public SagaStatus(SagaStatusEnum status)
            : this(status, string.Empty)
        { }

        public SagaStatus(SagaStatusEnum status, string statusReason)
        {
            Status = status;
            StatusReason = statusReason;
        }

        public SagaStatusEnum Status { get; private set; }
        public string StatusReason { get; private set; }

        public bool IsSuccess() => Status == SagaStatusEnum.Success;
        public bool IsFailed() => Status == SagaStatusEnum.Failed;
        public bool IsInProgress() => Status == SagaStatusEnum.InProgress;
        public bool IsNotStarted() => Status == SagaStatusEnum.NotStarted;
        public bool IsCancelled() => Status == SagaStatusEnum.Cancelled;

        public override string ToString()
        {
            var reason = string.IsNullOrWhiteSpace(StatusReason) ? "" : $", Reason: {StatusReason}";
            return $"Status: {Status}{reason}";
        }
    }
}
