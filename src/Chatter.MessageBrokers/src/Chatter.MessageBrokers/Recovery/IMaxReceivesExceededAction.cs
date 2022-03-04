using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public interface IMaxReceivesExceededAction
    {
        Task ExecuteAsync(FailureContext failureContext);
    }
}
