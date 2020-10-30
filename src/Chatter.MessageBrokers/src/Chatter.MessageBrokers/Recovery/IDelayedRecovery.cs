using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public interface IDelayedRecovery
    {
        Task Delay(FailureContext failureContext);
    }
}
