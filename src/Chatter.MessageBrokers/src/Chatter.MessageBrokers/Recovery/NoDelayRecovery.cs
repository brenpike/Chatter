using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    class NoDelayRecovery : IDelayedRecoveryStrategy
    {
        public Task Execute(FailureContext context) => Task.CompletedTask;
    }
}
