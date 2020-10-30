using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public interface IRecoveryAction
    {
        Task<RecoveryState> Execute(FailureContext failureContext);
    }
}
