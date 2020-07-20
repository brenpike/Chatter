using Chatter.CQRS.Context;
using System;

namespace Chatter.MessageBrokers.Saga
{
    public sealed class SagaContext : IContainContext
    {
        public string SagaId { get; }
        public SagaStatus Status { get; private set; }
        public string ReceiverPath { get; }
        public string DestinationPath { get; }
        public ContextContainer Container { get; }

        internal DateTime? PersistedAtUtc { get; set; } = null;//TODO: temporary for in memory saga persistance clean up

        internal SagaContext()
            : this(Guid.NewGuid().ToString(), string.Empty, string.Empty, SagaStatusEnum.NotStarted)
        { }

        internal SagaContext(string sagaId, string receiverPath, string destinationPath, SagaStatusEnum sagaStatus, string statusReason = "", ContextContainer parentContainer = null)
        {
            Container = new ContextContainer(parentContainer);
            Status = new SagaStatus(sagaStatus, statusReason);
            SagaId = sagaId ?? throw new ArgumentNullException(nameof(sagaId));
            ReceiverPath = receiverPath;
            DestinationPath = destinationPath;
        }

        internal void Success(string reason = "")
        {
            Status = new SagaStatus(SagaStatusEnum.Success, reason);
        }

        internal void Fail(string reason = "")
        {
            Status = new SagaStatus(SagaStatusEnum.Failed, reason);
        }

        internal void InProgress(string reason = "")
        {
            Status = new SagaStatus(SagaStatusEnum.InProgress, reason);
        }

        internal void Cancel(string reason = "")
        {
            Status = new SagaStatus(SagaStatusEnum.Cancelled, reason);
        }
    }
}
