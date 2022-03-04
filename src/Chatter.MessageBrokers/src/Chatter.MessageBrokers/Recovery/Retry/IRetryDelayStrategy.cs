using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    public interface IRetryDelayStrategy
    {
        void Execute(FailureContext failureContext) => ExecuteAsync(failureContext).GetAwaiter().GetResult();
        Task ExecuteAsync(FailureContext failureContext);

        void Execute(int deliveryCount) => ExecuteAsync(deliveryCount).GetAwaiter().GetResult();
        Task ExecuteAsync(int deliveryCount);
    }
}
