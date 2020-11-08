using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Recovery;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    public interface IFailedReceiveRecoverer
    {
        Task<RecoveryState> Execute(FailureContext failureContext);
    }
}
