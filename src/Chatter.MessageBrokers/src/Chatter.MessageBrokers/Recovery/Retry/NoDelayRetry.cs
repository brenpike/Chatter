using Chatter.MessageBrokers.Context;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    class NoDelayRetry : IRetryDelayStrategy
    {
        public Task ExecuteAsync(FailureContext failureContext, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExecuteAsync(FailureContext failureContext) => ExecuteAsync(failureContext, CancellationToken.None);

        public Task ExecuteAsync(int deliveryCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExecuteAsync(int deliveryCount) => ExecuteAsync(deliveryCount, CancellationToken.None);
    }
}
