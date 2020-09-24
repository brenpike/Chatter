using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public interface IOutboxProcessor
    {
        Task ProcessBatch(Guid batchId);
        Task Process(OutboxMessage outboxMessage);
    }
}
