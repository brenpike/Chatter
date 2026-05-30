using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    class NoDelayRetry : IRetryDelayStrategy
    {
        public Task ExecuteAsync(FailureContext failureContext) => Task.CompletedTask;
        public Task ExecuteAsync(int deliveryCount) => Task.CompletedTask;
#if NETSTANDARD2_0
        public void Execute(FailureContext failureContext) { }
        public void Execute(int deliveryCount) { }
#endif
    }
}
