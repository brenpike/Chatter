using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    public interface IRetryDelayStrategy
    {
        void Execute(FailureContext failureContext)
#if !NETSTANDARD2_0
            => ExecuteAsync(failureContext).GetAwaiter().GetResult()
#endif
            ;
        Task ExecuteAsync(FailureContext failureContext);

        void Execute(int deliveryCount)
#if !NETSTANDARD2_0
            => ExecuteAsync(deliveryCount).GetAwaiter().GetResult()
#endif
            ;
        Task ExecuteAsync(int deliveryCount);
    }
}
