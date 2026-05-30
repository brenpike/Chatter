using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    class ConstantDelayRetry : IRetryDelayStrategy
    {
        private readonly int _constantDelayInMilliseconds;

        public ConstantDelayRetry(int constantDelayInMilliseconds)
            => _constantDelayInMilliseconds = constantDelayInMilliseconds;

        public Task ExecuteAsync(FailureContext failureContext) => ExecuteAsync(_constantDelayInMilliseconds);
        public Task ExecuteAsync(int deliveryCount) => Task.Delay(_constantDelayInMilliseconds);
#if NETSTANDARD2_0
        public void Execute(FailureContext failureContext) => ExecuteAsync(failureContext).GetAwaiter().GetResult();
        public void Execute(int deliveryCount) => ExecuteAsync(deliveryCount).GetAwaiter().GetResult();
#endif
    }
}
