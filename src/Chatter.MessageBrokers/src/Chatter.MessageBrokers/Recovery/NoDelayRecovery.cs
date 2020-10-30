using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    class NoDelayRecovery : IDelayedRecovery
    {
        public Task Delay(FailureContext context) => Task.CompletedTask;
    }
}
