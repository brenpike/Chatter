using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    public interface IPersistanceTransaction : IDisposable, IAsyncDisposable
    {
        public string TransactionId { get; }
        Task CommitAsync(CancellationToken cancellationToken = default);
        Task RollbackAsync(CancellationToken cancellationToken = default);
    }
}
