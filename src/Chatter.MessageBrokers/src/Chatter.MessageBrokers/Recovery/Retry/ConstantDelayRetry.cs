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
    }
}
