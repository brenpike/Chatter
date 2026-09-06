using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    class ConstantDelayRetry : IRetryDelayStrategy
    {
        private readonly int _constantDelayInMilliseconds;

        public ConstantDelayRetry(int constantDelayInMilliseconds)
            => _constantDelayInMilliseconds = constantDelayInMilliseconds;

        public Task ExecuteAsync(FailureContext failureContext, CancellationToken cancellationToken)
            => ExecuteAsync(_constantDelayInMilliseconds, cancellationToken);
        public Task ExecuteAsync(FailureContext failureContext) => ExecuteAsync(failureContext, CancellationToken.None);

        public Task ExecuteAsync(int deliveryCount, CancellationToken cancellationToken)
            => Task.Delay(_constantDelayInMilliseconds, cancellationToken);
        public Task ExecuteAsync(int deliveryCount) => ExecuteAsync(deliveryCount, CancellationToken.None);
    }
}
