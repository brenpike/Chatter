using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Recovery.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    class ConstantDelayRecovery : IDelayedRecovery
    {
        private readonly RecoveryOptions _options;

        public ConstantDelayRecovery(RecoveryOptions options) 
            => _options = options;

        public Task Delay(FailureContext failureContext) 
            => Task.Delay(_options.ConstantDelayInMilliseconds);
    }
}
