using Chatter.MessageBrokers.Context;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    public interface IRetryDelayStrategy
    {
        void Execute(FailureContext failureContext)
            => ExecuteAsync(failureContext).GetAwaiter().GetResult();
        Task ExecuteAsync(FailureContext failureContext, CancellationToken cancellationToken);
        Task ExecuteAsync(FailureContext failureContext)
            => ExecuteAsync(failureContext, CancellationToken.None);

        void Execute(int deliveryCount)
            => ExecuteAsync(deliveryCount).GetAwaiter().GetResult();
        Task ExecuteAsync(int deliveryCount, CancellationToken cancellationToken);
        Task ExecuteAsync(int deliveryCount)
            => ExecuteAsync(deliveryCount, CancellationToken.None);
    }
}
