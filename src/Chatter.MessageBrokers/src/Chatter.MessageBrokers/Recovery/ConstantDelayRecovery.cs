using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    class ConstantDelayRecovery : IDelayedRecoveryStrategy
    {
        private readonly int _constantDelayInMilliseconds;

        public ConstantDelayRecovery(int constantDelayInMilliseconds)
            => _constantDelayInMilliseconds = constantDelayInMilliseconds;

        public Task Execute(FailureContext failureContext)
            => Task.Delay(_constantDelayInMilliseconds);
    }
}
