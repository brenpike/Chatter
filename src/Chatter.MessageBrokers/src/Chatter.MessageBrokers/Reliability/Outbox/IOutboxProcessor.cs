using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public interface IOutboxProcessor
    {
        Task ProcessBatch(Guid batchId, CancellationToken cancellationToken = default);
        Task Process(OutboxMessage outboxMessage, CancellationToken cancellationToken = default);
    }
}
