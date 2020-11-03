using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public interface ICriticalFailureNotifier
    {
        Task Notify(FailureContext failureContext);
    }
}
