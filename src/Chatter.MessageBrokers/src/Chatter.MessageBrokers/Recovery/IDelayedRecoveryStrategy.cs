using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public interface IDelayedRecoveryStrategy
    {
        Task Execute(FailureContext failureContext);
    }
}
